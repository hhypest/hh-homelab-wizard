using System.Net;
using System.Net.Sockets;

namespace HhHomelab.Wizard.Domain;

/// <summary>Explicit local choices for the first qBittorrent preview. No credentials or defaults
/// from another NAS. Lexical validation does not prove reachability, account access or DSM ACL.</summary>
public sealed class QbittorrentInstallationRequest
{
    public QbittorrentInstallationRequest(Guid installationId, SshConnectionTarget target, MediaPathLayout layout,
        string nasAddress, string bindAddress, int webUiPort, int torrentPort, string networkName,
        int userId, int groupId, string umask, string timeZone, long dataFreeBytes, long configurationFreeBytes)
    {
        if (installationId == Guid.Empty || target is not { IsValid: true } || layout is null)
            throw new ArgumentException("plan.request.identity");
        if (!IsIpv4(nasAddress) || IPAddress.IsLoopback(IPAddress.Parse(nasAddress))
            || nasAddress == "0.0.0.0" || nasAddress == "255.255.255.255"
            || IPAddress.Parse(nasAddress).GetAddressBytes()[0] is >= 224 or 0)
            throw new ArgumentException("plan.request.address");
        if (bindAddress != "0.0.0.0" && bindAddress != "127.0.0.1" && bindAddress != nasAddress)
            throw new ArgumentException("plan.request.bind-address");
        if (webUiPort is < 1 or > 65535 || torrentPort is < 1 or > 65535 || webUiPort == torrentPort)
            throw new ArgumentException("plan.request.ports");
        if (networkName is not { Length: > 0 and <= 63 } || !char.IsAsciiLetterOrDigit(networkName[0])
            || networkName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-'))
            throw new ArgumentException("plan.request.network");
        if (userId <= 0 || groupId <= 0 || umask is not { Length: 3 } || umask.Any(character => character is < '0' or > '7'))
            throw new ArgumentException("plan.request.permissions");
        if (timeZone is not { Length: > 0 and <= 64 } || timeZone.Split('/').Any(part => part is "" or "." or "..")
            || timeZone.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '/' and not '_' and not '-' and not '+'))
            throw new ArgumentException("plan.request.timezone");
        if (dataFreeBytes <= 0 || configurationFreeBytes <= 0 || dataFreeBytes > long.MaxValue - configurationFreeBytes)
            throw new ArgumentException("plan.request.space");
        InstallationId = installationId;
        Target = target;
        Layout = layout;
        NasAddress = nasAddress;
        BindAddress = bindAddress;
        WebUiPort = webUiPort;
        TorrentPort = torrentPort;
        NetworkName = networkName;
        UserId = userId;
        GroupId = groupId;
        Umask = umask;
        TimeZone = timeZone;
        DataFreeBytes = dataFreeBytes;
        ConfigurationFreeBytes = configurationFreeBytes;
    }

    public Guid InstallationId { get; }
    public SshConnectionTarget Target { get; }
    public MediaPathLayout Layout { get; }
    public string NasAddress { get; }
    public string BindAddress { get; }
    public int WebUiPort { get; }
    public int TorrentPort { get; }
    public string NetworkName { get; }
    public int UserId { get; }
    public int GroupId { get; }
    public string Umask { get; }
    public string TimeZone { get; }
    public long DataFreeBytes { get; }
    public long ConfigurationFreeBytes { get; }

    private static bool IsIpv4(string value) => IPAddress.TryParse(value, out var address)
        && address.AddressFamily == AddressFamily.InterNetwork && address.ToString() == value;

    public override string ToString() => "plan.request";
}
