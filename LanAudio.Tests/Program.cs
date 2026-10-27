using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AudioPlaybackConnector.Lan;
using NAudio.CoreAudioApi;
using NAudio.Wave;

internal static class Program
{
    private const string Code = "TEST-0123456789AB";
    private static int assertions;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            ProtocolTests();
            ConnectionTests();
            DiscoveryTests();
            BufferTests();
            await NetworkTests();
            await DiscoveryNetworkTest();
            if (args.Contains("--audio")) { await AudioDeviceTests(); await UiLifecycleTest(); }
            Console.WriteLine($"PASS: {assertions} assertions. Audio-device checks: {args.Contains("--audio")}.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static byte[] Samples(int frames, short value)
    {
        byte[] bytes = new byte[frames * AudioProtocol.BlockAlign];
        for (int i = 0; i < bytes.Length; i += 2) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i), value);
        return bytes;
    }

    private static void ProtocolTests()
    {
        using var tx = new AudioProtocol(Code);
        using var rx = new AudioProtocol(Code);
        using var wrong = new AudioProtocol("wrong-code-12345");
        byte[] session = RandomNumberGenerator.GetBytes(12);
        byte[] pcm = Samples(280, 12345);
        byte[] encoded = tx.Encode(pcm, session, 15, 4200);
        AudioPacket decoded = rx.Decode(encoded)!;
        Check(encoded.Length <= 1200, "datagram fits below typical Ethernet MTU");
        Check(decoded is not null && decoded.Sequence == 15 && decoded.Frame == 4200 && decoded.Pcm.SequenceEqual(pcm), "authenticated PCM round trip");
        Check(wrong.Decode(encoded) is null, "wrong code rejected");
        foreach (int offset in new[] { 0, 4, 12, 20, 32, 36, 40, encoded.Length - 1 })
        {
            byte[] tampered = (byte[])encoded.Clone(); tampered[offset] ^= 1;
            Check(rx.Decode(tampered) is null, "tampered header/payload/tag rejected at " + offset);
        }
        Check(rx.Decode(encoded.AsSpan(0, encoded.Length - 1)) is null, "truncation rejected");
        Check(rx.Decode(new byte[65507]) is null, "oversized packet rejected");
        Check(!tx.Encode(pcm, session, 16, 4480).AsSpan(AudioProtocol.HeaderSize).SequenceEqual(encoded.AsSpan(AudioProtocol.HeaderSize)), "unique sequence changes ciphertext");
        foreach (byte[] fuzz in Enumerable.Range(0, 200).Select(i => RandomNumberGenerator.GetBytes(i * 7)))
            Check(rx.Decode(fuzz) is null, "random network data rejected");
        Console.WriteLine("PASS protocol: round trip, framing, authentication, tampering, fuzz input.");
    }

    private static void BufferTests()
    {
        var buffer = new JitterBuffer(20);
        // Receive in reverse order and repeat one packet.
        for (int i = 5; i >= 0; i--) buffer.Push(new AudioPacket("a", (uint)i, i * 240, Samples(240, (short)(1000 + i * 100))));
        buffer.Push(new AudioPacket("a", 2, 480, Samples(240, 1200)));
        byte[] output = new byte[960 * 4]; buffer.Read(output, 0, output.Length);
        Check(BinaryPrimitives.ReadInt16LittleEndian(output) == 1000, "reorder preserves initial sample");
        Check(BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(300 * 4)) is >= 1099 and <= 1101, "reorder preserves later samples");
        Check(buffer.Snapshot().DuplicatePackets == 1, "duplicate packet rejected");
        buffer.Push(new AudioPacket("a", 0, 0, Samples(240, 30000)));
        Check(buffer.Snapshot().LatePackets == 1, "played audio cannot be replayed");

        var gap = new JitterBuffer(20);
        for (int i = 0; i < 8; i++) if (i != 2) gap.Push(new AudioPacket("a", (uint)i, i * 240, Samples(240, 5000)));
        byte[] gapped = new byte[1200 * 4]; gap.Read(gapped, 0, gapped.Length);
        Check(BinaryPrimitives.ReadInt16LittleEndian(gapped.AsSpan(550 * 4)) == 0, "lost packets become silence");
        Check(BinaryPrimitives.ReadInt16LittleEndian(gapped.AsSpan(850 * 4)) > 4900, "playback resumes after a missing packet");
        Check(gap.Snapshot().MissingFrames > 200, "loss metric counts missing audio");

        var bound = new JitterBuffer(100);
        for (int i = 0; i < 400; i++) bound.Push(new AudioPacket("a", (uint)i, i * 240, Samples(240, 1234)));
        Check(bound.Snapshot().Milliseconds <= 350, "queue stays bounded under backlog");
        Check(bound.Snapshot().Resyncs > 0, "excess latency causes a resynchronization");
        bound.Volume = 0;
        byte[] muted = new byte[240 * 4]; bound.Read(muted, 0, muted.Length);
        Check(muted.All(b => b == 0), "receiver volume zero outputs silence");

        var clock = new JitterBuffer(100);
        long frame = 0;
        for (int i = 0; i < 22; i++) { clock.Push(new("a", 0, frame, Samples(240, 2222))); frame += 240; }
        byte[] tick = new byte[240 * 4];
        for (int i = 0; i < 6000; i++)
        {
            // Simulate a sender whose clock runs 0.1% faster for 30 seconds.
            int frames = i % 4 == 0 ? 241 : 240;
            clock.Push(new("a", 0, frame, Samples(frames, 2222))); frame += frames;
            clock.Read(tick, 0, tick.Length);
        }
        BufferSnapshot snapshot = clock.Snapshot();
        Check(snapshot.Milliseconds is > 60 and < 160 && snapshot.Resyncs == 0, "clock adaptation avoids accumulating latency");
        Check(snapshot.Rate is >= .998 and <= 1.002, "adaptive read rate stays bounded");
        Console.WriteLine($"PASS buffer: reordering, loss, replay, backlog, volume, 30-second clock simulation ({snapshot.Milliseconds:F1} ms).");
    }

    private static void ConnectionTests()
    {
        foreach (string code in new[] { Code, "with|separator%", "中文连接码一二三四五六" })
        {
            var info = new ConnectionInfo(IPAddress.Parse("192.168.1.2"), 29864, code);
            Check(ConnectionInfo.Parse(info.Serialize()) == info, "connection info round trip including escaped code");
        }
        foreach (string bad in new[] { "hello", "APCL1|0.0.0.0|29864|12345678", "APCL1|192.168.1.2|80|12345678", "APCL1|192.168.1.2|29864|short" })
        {
            bool rejected = false;
            try { ConnectionInfo.Parse(bad); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "malformed connection info rejected");
        }
        Console.WriteLine("PASS connection info: escaped codes, address and port validation.");
        Check(new AudioProtocol(AudioProtocol.DefaultPairingCode) is not null, "shared default pairing code is valid");
    }

    private static void DiscoveryTests()
    {
        var home = IPAddress.Parse("192.168.1.20");
        byte[] packet = DiscoveryProtocol.Encode("客厅电脑", home, 29864);
        DiscoveredReceiver decoded = DiscoveryProtocol.Decode(packet, home)!;
        Check(decoded.Name == "客厅电脑" && decoded.Address.Equals(home) && decoded.AudioPort == 29864, "discovery announcement round trip");
        Check(DiscoveryProtocol.Decode(packet, IPAddress.Parse("192.168.1.21")) is null, "announcement from a different source is ignored");
        Check(DiscoveryProtocol.Decode(packet) is not null, "decode without an observed source stays available for tests");
        foreach (string rejected in new[] { "1.2.3.4", "8.8.8.8", "169.254.1.1", "224.0.0.1", "127.0.0.1", "0.0.0.0" })
            Check(DiscoveryProtocol.Decode(DiscoveryProtocol.Encode("外来设备", IPAddress.Parse(rejected), 29864)) is null, rejected + " is not announced");
        byte[] tampered = (byte[])packet.Clone(); tampered[5] ^= 1;
        Check(DiscoveryProtocol.Decode(tampered, home) is null, "non-discovery datagram ignored");
        byte[] renamed = (byte[])packet.Clone(); renamed[12] = 0x01;
        Check(DiscoveryProtocol.Decode(renamed, home) is null, "control characters in a device name rejected");
        Check(new DiscoveredReceiver("甲", home, 29864, DateTime.UtcNow.AddSeconds(-9)).IsStale(DateTime.UtcNow), "silent receiver expires");
        Check(!new DiscoveredReceiver("甲", home, 29864, DateTime.UtcNow).IsStale(DateTime.UtcNow), "fresh receiver stays listed");
        Check(DiscoveryAnnouncer.BroadcastOf(home, IPAddress.Parse("255.255.255.0")).Equals(IPAddress.Parse("192.168.1.255")), "directed broadcast uses the interface mask");
        Console.WriteLine("PASS discovery: announcement framing, source check, private addresses, expiry.");
    }

    private static async Task DiscoveryNetworkTest()
    {
        using var announcer = new DiscoveryAnnouncer(32123, "测试接收");
        using var listener = new DiscoveryListener(includeLocal: true);
        var timeout = Stopwatch.StartNew();
        DiscoveredReceiver[] found = Array.Empty<DiscoveredReceiver>();
        while (found.Length == 0 && timeout.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(200);
            found = listener.Snapshot();
        }
        Check(listener.Failure is null, "discovery listener stays healthy");
        Check(found.Length > 0 && found.All(r => r.Name == "测试接收" && r.AudioPort == 32123), "real UDP broadcast reaches a listener on this computer");
        Check(found.All(r => DiscoveryProtocol.IsPrivateLan(r.Address)), "discovered addresses stay on private LAN ranges");
        Console.WriteLine($"PASS discovery network: {found.Length} local interface(s) announced over UDP {DiscoveryProtocol.Port}.");
    }

    private static async Task NetworkTests()
    {
        using var receiver = new AudioReceiver(null, 0, Code, 20, IPAddress.Loopback);
        using var udp = new UdpClient(); udp.Connect(IPAddress.Loopback, receiver.BoundPort);
        using var protocol = new AudioProtocol(Code);
        byte[] session = RandomNumberGenerator.GetBytes(12);
        for (int i = 5; i >= 0; i--)
        {
            byte[] packet = protocol.Encode(Samples(240, (short)(1000 + i * 100)), session, (uint)i, i * 240);
            await udp.SendAsync(packet);
        }
        using var incorrect = new AudioProtocol("incorrect-connection-code");
        await udp.SendAsync(incorrect.Encode(Samples(240, 0), session, 10, 2400));
        byte[] secondSession = RandomNumberGenerator.GetBytes(12);
        await udp.SendAsync(protocol.Encode(Samples(240, 0), secondSession, 0, 0));
        var timeout = Stopwatch.StartNew();
        while (receiver.Snapshot().Packets < 6 && timeout.ElapsedMilliseconds < 3000) await Task.Delay(10);
        await Task.Delay(100);
        ReceiverSnapshot s = receiver.Snapshot();
        Check(s.Packets == 6, "real UDP receives six authenticated packets");
        Check(s.InvalidPackets == 1, "real UDP rejects incorrect connection code");
        Check(s.OtherSourcePackets == 1, "active stream cannot be replaced by another session");
        byte[] output = new byte[960 * 4]; receiver.Buffer.Read(output, 0, output.Length);
        Check(BinaryPrimitives.ReadInt16LittleEndian(output) == 1000, "real UDP receiver reconstructs original audio");
        Check(receiver.Failure is null, "receive loop stays healthy");
        Console.WriteLine("PASS network: encrypted UDP loopback, order recovery, wrong code, active-session lock.");
    }

    private static async Task AudioDeviceTests()
    {
        List<AudioDevice> devices = AudioDevices.ListOutputs();
        Check(devices.Count > 0, "real playback device exists");
        Console.WriteLine("Audio devices: " + string.Join(" | ", devices.Select(d => d.Name)));
        string deviceId = devices[0].Id;
        // Render zeros only: exercise the real output path without sounding a test tone.
        using (var rendered = new AudioReceiver(deviceId, 0, Code, 60, IPAddress.Loopback))
        using (var udp = new UdpClient())
        using (var protocol = new AudioProtocol(Code))
        {
            udp.Connect(IPAddress.Loopback, rendered.BoundPort);
            byte[] session = RandomNumberGenerator.GetBytes(12);
            for (uint i = 0; i < 120; i++)
            {
                await udp.SendAsync(protocol.Encode(new byte[960], session, i, i * 240));
                await Task.Delay(5);
            }
            Check(rendered.Snapshot().Packets == 120, "real WASAPI playback receives UDP frames");
            Check(rendered.Failure is null, "real WASAPI output starts successfully");
        }
        // Keep the Windows audio engine active with silence, then capture and send
        // its real loopback samples to a null-output listener. No raw recording is saved.
        using var device = AudioDevices.Open(deviceId);
        using var silentOutput = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
        silentOutput.Init(new SilenceProvider(new WaveFormat(48000, 16, 2)));
        silentOutput.Play();
        using var listener = new AudioReceiver(null, 0, Code, 60, IPAddress.Loopback);
        using var sender = new AudioSender(deviceId, IPAddress.Loopback, listener.BoundPort, Code);
        await Task.Delay(1500);
        Check(sender.Failure is null, "real WASAPI loopback capture supports requested format");
        Check(listener.Snapshot().Packets > 10, "real capture flows through encrypted UDP to receiver");
        Console.WriteLine($"PASS audio hardware: silent WASAPI output and real loopback → encrypted UDP ({listener.Snapshot().Packets} packets). No raw audio saved.");
    }

    private static async Task UiLifecycleTest()
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new MainForm { ShowInTaskbar = false, Opacity = 0 };
                form.Shown += (_, _) => form.BeginInvoke(new Action(form.Close));
                Application.Run(form);
                completed.SetResult();
            }
            catch (Exception ex) { completed.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(true, "idle form closes without leaving its message loop alive");
        Console.WriteLine("PASS UI lifecycle: open and close releases the window/message loop.");
    }
}
