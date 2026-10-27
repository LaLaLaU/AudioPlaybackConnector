using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioPlaybackConnector.Lan;

internal sealed class AudioSender : IDisposable
{
    private readonly UdpClient socket;
    private readonly AudioProtocol protocol;
    private readonly byte[] session = RandomNumberGenerator.GetBytes(12);
    private readonly MMDevice device;
    private readonly LoopbackCapture capture;
    private uint sequence;
    private long frames, packetCount, bytes, lastData;
    private int stopped;
    private float peak;
    private string? failure;
    internal string? Failure => Volatile.Read(ref failure);

    internal AudioSender(string deviceId, IPAddress target, int port, string code)
    {
        protocol = new AudioProtocol(code);
        socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            device = AudioDevices.Open(deviceId);
            capture = new LoopbackCapture(device);
            socket.Client.SendBufferSize = 256 * 1024;
            socket.Connect(target, port);
            capture.DataAvailable += OnData;
            capture.RecordingStopped += (_, e) => { if (e.Exception is not null) Volatile.Write(ref failure, e.Exception.Message); };
            capture.StartRecording();
        }
        catch
        {
            capture?.Dispose(); device?.Dispose(); socket.Dispose(); protocol.Dispose();
            throw;
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (Volatile.Read(ref stopped) != 0 || e.BytesRecorded == 0) return;
        try
        {
            int count = e.BytesRecorded - e.BytesRecorded % AudioProtocol.BlockAlign;
            int max = 0;
            for (int i = 0; i < count; i += 8)
                max = Math.Max(max, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(e.Buffer.AsSpan(i, 2))));
            Volatile.Write(ref peak, max / 32768f);
            for (int offset = 0; offset < count; offset += AudioProtocol.MaxPayload)
            {
                if (sequence == uint.MaxValue) throw new InvalidOperationException("发送会话已达到上限，请停止后重新开始。");
                int length = Math.Min(AudioProtocol.MaxPayload, count - offset);
                byte[] datagram = protocol.Encode(e.Buffer.AsSpan(offset, length), session, sequence++, frames);
                socket.Send(datagram, datagram.Length);
                frames += length / AudioProtocol.BlockAlign;
                Interlocked.Increment(ref packetCount);
                Interlocked.Add(ref bytes, datagram.Length);
            }
            Interlocked.Exchange(ref lastData, Stopwatch.GetTimestamp());
        }
        catch (Exception ex)
        {
            Volatile.Write(ref failure, ex.Message);
            capture.StopRecording();
        }
    }

    internal SenderSnapshot Snapshot() => new(Interlocked.Read(ref packetCount), Interlocked.Read(ref bytes),
        Volatile.Read(ref peak), Stopwatch.GetElapsedTime(Interlocked.Read(ref lastData)).TotalMilliseconds < 1500);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        capture.Dispose(); // Joins capture thread before disposing its socket and cipher.
        socket.Dispose();
        protocol.Dispose();
        device.Dispose();
    }
}

internal sealed record SenderSnapshot(long Packets, long Bytes, float Peak, bool HasRecentAudio);

internal sealed class SwitchingWaveProvider : IWaveProvider
{
    private JitterBuffer buffer;
    public WaveFormat WaveFormat { get; } = new(AudioProtocol.SampleRate, 16, AudioProtocol.Channels);
    internal SwitchingWaveProvider(JitterBuffer buffer) => this.buffer = buffer;
    internal JitterBuffer Buffer { get => Volatile.Read(ref buffer); set => Volatile.Write(ref buffer, value); }
    public int Read(byte[] data, int offset, int count) => Buffer.Read(data, offset, count);
}

internal sealed class AudioReceiver : IDisposable
{
    private readonly UdpClient socket;
    private readonly AudioProtocol protocol;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task receiveTask;
    private readonly WasapiOut? output;
    private readonly MMDevice? device;
    private readonly IPAddress? expectedSource;
    private readonly SwitchingWaveProvider provider;
    private readonly int bufferMs;
    private readonly HashSet<string> retiredSessions = new();
    private readonly DiscoveryAnnouncer announcer;
    private string? session, peer, failure;
    private IPAddress? source;
    private long packets, bytes, invalid, otherSource, lastData;
    private int stopped;
    internal string? Failure => Volatile.Read(ref failure);
    internal int BoundPort => ((IPEndPoint)socket.Client.LocalEndPoint!).Port;
    internal JitterBuffer Buffer => provider.Buffer;

    internal AudioReceiver(string? outputDevice, int port, string code, int bufferMs, IPAddress? expectedSource = null)
    {
        this.expectedSource = expectedSource;
        this.bufferMs = bufferMs;
        protocol = new AudioProtocol(code);
        provider = new SwitchingWaveProvider(new JitterBuffer(bufferMs));
        socket = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            socket.ExclusiveAddressUse = true;
            socket.Client.ReceiveBufferSize = 1024 * 1024;
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            announcer = new DiscoveryAnnouncer(BoundPort);
            if (outputDevice is not null)
            {
                device = AudioDevices.Open(outputDevice);
                output = new WasapiOut(device, AudioClientShareMode.Shared, true, 40);
                output.PlaybackStopped += (_, e) => { if (e.Exception is not null) Volatile.Write(ref failure, e.Exception.Message); };
                output.Init(provider);
                output.Play();
            }
            receiveTask = Task.Run(ReceiveLoop);
        }
        catch
        {
            output?.Dispose(); device?.Dispose(); socket.Dispose(); protocol.Dispose(); cancellation.Dispose();
            announcer?.Dispose();
            throw;
        }
    }

    private async Task ReceiveLoop()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                UdpReceiveResult received = await socket.ReceiveAsync(cancellation.Token).ConfigureAwait(false);
                if (expectedSource is not null && !received.RemoteEndPoint.Address.Equals(expectedSource))
                { Interlocked.Increment(ref otherSource); continue; }
                AudioPacket? packet = protocol.Decode(received.Buffer);
                if (packet is null) { Interlocked.Increment(ref invalid); continue; }
                long now = Stopwatch.GetTimestamp();
                bool idle = Stopwatch.GetElapsedTime(Interlocked.Read(ref lastData), now).TotalSeconds >= 3;
                if (packet.Session != session)
                {
                    if (retiredSessions.Contains(packet.Session) || (session is not null && !idle))
                    { Interlocked.Increment(ref otherSource); continue; }
                    if (session is not null)
                    {
                        retiredSessions.Add(session);
                        // Bound memory without accepting retired sessions again in a long-running listener.
                        if (retiredSessions.Count > 4096) throw new InvalidOperationException("接收会话数量已达上限，请重新开始接收。");
                    }
                    float volume = provider.Buffer.Volume;
                    provider.Buffer = new JitterBuffer(bufferMs) { Volume = volume };
                    session = packet.Session;
                    source = received.RemoteEndPoint.Address;
                    Volatile.Write(ref peer, source.ToString());
                }
                if (!received.RemoteEndPoint.Address.Equals(source))
                { Interlocked.Increment(ref otherSource); continue; }
                provider.Buffer.Push(packet);
                Interlocked.Increment(ref packets);
                Interlocked.Add(ref bytes, received.Buffer.Length);
                Interlocked.Exchange(ref lastData, now);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { Volatile.Write(ref failure, ex.Message); }
    }

    internal ReceiverSnapshot Snapshot() => new(Interlocked.Read(ref packets), Interlocked.Read(ref bytes),
        Interlocked.Read(ref invalid), Interlocked.Read(ref otherSource), Volatile.Read(ref peer),
        Stopwatch.GetElapsedTime(Interlocked.Read(ref lastData)).TotalMilliseconds < 2500, provider.Buffer.Snapshot());

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        cancellation.Cancel();
        socket.Dispose();
        receiveTask.GetAwaiter().GetResult();
        output?.Stop(); output?.Dispose(); device?.Dispose();
        protocol.Dispose(); cancellation.Dispose();
        announcer.Dispose();
    }
}

internal sealed record ReceiverSnapshot(long Packets, long Bytes, long InvalidPackets, long OtherSourcePackets, string? Peer, bool HasRecentAudio, BufferSnapshot Buffer);
