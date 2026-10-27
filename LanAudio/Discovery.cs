using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AudioPlaybackConnector.Lan;

// Receivers announce themselves on a dedicated UDP port so a sender can pick one
// instead of typing its address. Announcements carry no pairing code.
internal static class DiscoveryProtocol
{
    internal const int Port = 29865;
    private static ReadOnlySpan<byte> Magic => "APCL"u8;

    internal static byte[] Encode(string name, IPAddress address, int audioPort)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Trim().Length > 64) throw new ArgumentException("设备名不能超过 64 个字符。");
        if (audioPort is < 1024 or > 65535) throw new ArgumentException("音频端口无效。");
        if (address.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("发现只广播 IPv4 地址。");
        byte[] label = Encoding.UTF8.GetBytes(name.Trim());
        byte[] packet = new byte[12 + label.Length];
        Magic.CopyTo(packet);
        packet[4] = 1;
        packet[5] = (byte)'D';
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), (ushort)audioPort);
        address.GetAddressBytes().CopyTo(packet, 8);
        label.CopyTo(packet, 12);
        return packet;
    }

    internal static DiscoveredReceiver? Decode(ReadOnlySpan<byte> datagram, IPAddress? observedFrom = null)
    {
        if (datagram.Length is < 13 or > 12 + 192 || !datagram[..4].SequenceEqual(Magic) || datagram[4] != 1 || datagram[5] != (byte)'D')
            return null;
        int audioPort = BinaryPrimitives.ReadUInt16LittleEndian(datagram[6..]);
        if (audioPort is < 1024 or > 65535) return null;
        var announced = new IPAddress(datagram[8..12]);
        if (!IsPrivateLan(announced)) return null;
        // A receiver speaks for itself. A forged source address cannot advertise a different target.
        if (observedFrom is not null && !announced.Equals(observedFrom)) return null;
        string name;
        try { name = Encoding.UTF8.GetString(datagram[12..]).Trim(); }
        catch (ArgumentException) { return null; }
        if (name.Length is 0 or > 64 || name.Any(char.IsControl)) return null;
        return new DiscoveredReceiver(name, announced, audioPort, DateTime.UtcNow);
    }

    internal static bool IsPrivateLan(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        byte[] bytes = address.GetAddressBytes();
        if (bytes[0] == 0 || bytes[0] >= 224) return false;
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
    }
}

internal sealed record DiscoveredReceiver(string Name, IPAddress Address, int AudioPort, DateTime SeenUtc)
{
    internal bool IsStale(DateTime now) => now - SeenUtc > TimeSpan.FromSeconds(8);
    public override string ToString() => $"{Name}  ·  {Address}  ·  端口 {AudioPort}";
}

internal sealed class DiscoveryAnnouncer : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task task;
    private int stopped;

    internal DiscoveryAnnouncer(int audioPort, string? name = null)
    {
        if (audioPort is < 1024 or > 65535) throw new ArgumentException("音频端口无效。");
        string label = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
        task = Task.Run(() => Announce(label, audioPort, cancellation.Token));
    }

    private static async Task Announce(string name, int audioPort, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            do { Broadcast(name, audioPort); }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static void Broadcast(string name, int audioPort)
    {
        foreach (IPAddress broadcast in BroadcastAddresses())
        {
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
                socket.SendTo(DiscoveryProtocol.Encode(name, UnicastFor(broadcast), audioPort), new IPEndPoint(broadcast, DiscoveryProtocol.Port));
            }
            catch (SocketException) { }
        }
    }

    // Directed subnet broadcast, not 255.255.255.255: each announcement then names the interface it left on.
    private static IPAddress[] BroadcastAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && DiscoveryProtocol.IsPrivateLan(a.Address) && a.IPv4Mask is not null)
        .Select(a => BroadcastOf(a.Address, a.IPv4Mask))
        .Distinct()
        .ToArray();

    private static IPAddress UnicastFor(IPAddress broadcast) => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses)
        .First(a => a.Address.AddressFamily == AddressFamily.InterNetwork && a.IPv4Mask is not null && BroadcastOf(a.Address, a.IPv4Mask).Equals(broadcast))
        .Address;

    internal static IPAddress BroadcastOf(IPAddress address, IPAddress mask)
    {
        byte[] ip = address.GetAddressBytes(), bits = mask.GetAddressBytes();
        for (int i = 0; i < 4; i++) ip[i] |= (byte)~bits[i];
        return new IPAddress(ip);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        cancellation.Cancel();
        task.GetAwaiter().GetResult();
        cancellation.Dispose();
    }
}

internal sealed class DiscoveryListener : IDisposable
{
    private readonly UdpClient socket;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task task;
    private readonly object gate = new();
    private readonly Dictionary<string, DiscoveredReceiver> found = new();
    private readonly bool includeLocal;
    private int stopped;
    internal string? Failure { get; private set; }

    internal DiscoveryListener(bool includeLocal = false)
    {
        this.includeLocal = includeLocal;
        socket = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        try
        {
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryProtocol.Port));
            task = Task.Run(Listen);
        }
        catch
        {
            socket.Dispose();
            cancellation.Dispose();
            throw;
        }
    }

    internal DiscoveredReceiver[] Snapshot()
    {
        lock (gate)
        {
            DateTime now = DateTime.UtcNow;
            foreach (string stale in found.Where(pair => pair.Value.IsStale(now)).Select(pair => pair.Key).ToArray()) found.Remove(stale);
            return found.Values.OrderBy(r => r.Name, StringComparer.CurrentCulture).ThenBy(r => r.Address.ToString()).ToArray();
        }
    }

    private async Task Listen()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                UdpReceiveResult received = await socket.ReceiveAsync(cancellation.Token).ConfigureAwait(false);
                DiscoveredReceiver? receiver = DiscoveryProtocol.Decode(received.Buffer, received.RemoteEndPoint.Address);
                if (receiver is null || (!includeLocal && LocalNetwork.Addresses().Contains(receiver.Address.ToString()))) continue;
                lock (gate) found[receiver.Address.ToString()] = receiver;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { Failure = ex.Message; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) != 0) return;
        cancellation.Cancel();
        socket.Dispose();
        task.GetAwaiter().GetResult();
        cancellation.Dispose();
    }
}
