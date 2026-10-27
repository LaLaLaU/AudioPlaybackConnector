using System.Text.Json;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AudioPlaybackConnector.Lan;

internal sealed class Preferences
{
    internal static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AudioPlaybackConnectorLAN");
    private static string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public string TargetIp { get; set; } = "";
    public int Port { get; set; } = 29864;
    public int BufferMs { get; set; } = 120;
    public string ReceiverCode { get; set; } = AudioProtocol.DefaultPairingCode;
    public string SenderCode { get; set; } = AudioProtocol.DefaultPairingCode;
    public string SourceDeviceId { get; set; } = "";
    public string OutputDeviceId { get; set; } = "";
    public string AllowedSender { get; set; } = "";
    internal static Preferences Load()
    {
        try
        {
            Preferences loaded = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? new();
            // Settings saved before the shared default carry a random receiver code and no sender code.
            if (string.IsNullOrWhiteSpace(loaded.SenderCode))
            {
                loaded.ReceiverCode = AudioProtocol.DefaultPairingCode;
                loaded.SenderCode = AudioProtocol.DefaultPairingCode;
            }
            return loaded;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    internal void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
}

internal static class LocalNetwork
{
    internal static LocalAddress[] Endpoints() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => new
        {
            a.Address,
            InterfaceName = n.Name,
            HasGateway = n.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
        }))
        .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && !a.Address.ToString().StartsWith("169.254."))
        .OrderByDescending(a => (IsPrivate(a.Address) ? 100 : 0) + (a.HasGateway ? 50 : 0))
        .Select(a => new LocalAddress(a.Address.ToString(), a.InterfaceName)).DistinctBy(a => a.Address).ToArray();

    internal static string[] Addresses() => Endpoints().Select(a => a.Address).ToArray();

    private static bool IsPrivate(IPAddress ip)
    {
        byte[] bytes = ip.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168);
    }

    internal static IPAddress ParseV4(string text)
    {
        if (!IPAddress.TryParse(text.Trim(), out var ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
            ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast) || ip.GetAddressBytes()[0] >= 224)
            throw new ArgumentException("请输入有效的局域网 IPv4 地址，例如 192.168.1.2。");
        return ip;
    }
}

internal sealed record LocalAddress(string Address, string InterfaceName)
{
    public override string ToString() => $"{Address}  ·  {InterfaceName}";
}

internal sealed record ConnectionInfo(IPAddress Address, int Port, string Code)
{
    internal string Serialize() => $"APCL1|{Address}|{Port}|{Uri.EscapeDataString(Code.Trim())}";

    internal static ConnectionInfo Parse(string text)
    {
        string[] parts = text.Trim().Split('|');
        if (parts.Length != 4 || parts[0] != "APCL1" || !int.TryParse(parts[2], out int port) || port is < 1024 or > 65535)
            throw new ArgumentException("剪贴板中没有有效连接信息。请先在接收电脑点击“复制连接信息”。");
        IPAddress ip = LocalNetwork.ParseV4(parts[1]);
        string code = Uri.UnescapeDataString(parts[3]);
        using var validate = new AudioProtocol(code);
        return new(ip, port, code);
    }
}
