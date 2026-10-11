using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Explicit local settings. A class intentionally avoids record-generated secret-bearing ToString.</summary>
public sealed class HomeAssistantLocalSettings
{
    public required string NasAddress { get; init; }
    public required string TvBroadcastAddress { get; init; }
    public required string TvMac { get; init; }
    public required string JellyfinUserId { get; init; }
    public required string JellyfinToken { get; init; }
    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public int Elevation { get; init; }
}

/// <summary>Pinned v1.3.0 source transformations; not deployment or proof of device readiness.</summary>
public static partial class HomeAssistantLocalProfile
{
    public static async Task<HomeAssistantPayload> RenderAsync(string sourceDirectory,
        HomeAssistantLocalSettings settings, MonitorDockerPackage component, CancellationToken token = default)
    {
        Validate(settings);
        using var profileStream = typeof(HomeAssistantLocalProfile).Assembly.GetManifestResourceStream("ha-source-profile.json")!;
        using var reader = new StreamReader(profileStream, new UTF8Encoding(false, true));
        var profile = ProfileReader.Parse(await reader.ReadToEndAsync(token));
        if ((await ProfileReader.VerifySourceAsync(profile, sourceDirectory, token)).Count != 0)
            throw new InvalidDataException("ha.profile.source.invalid");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var path in new[] { "configuration.yaml", "automations.yaml", "scripts.yaml", "scenes.yaml",
            "packages/docker.yaml", "packages/monitoring.yaml", "packages/media_tv.yaml", "bin/docker_state.py", "bin/http_check.py" })
        {
            token.ThrowIfCancellationRequested();
            var sourcePath = "homeassistant/config/" + path;
            var content = (await File.ReadAllTextAsync(Path.Combine(sourceDirectory, sourcePath.Replace('/', Path.DirectorySeparatorChar)),
                new UTF8Encoding(false, true), token)).Replace("\r\n", "\n");
            // Hash the actual bytes being transformed, not just an earlier read of the checkout.
            if (Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))) !=
                profile.Artifacts.Single(a => a.Path == sourcePath).Sha256)
                throw new InvalidDataException("ha.profile.source.changed");
            if (path.EndsWith(".yaml", StringComparison.Ordinal))
                content = string.Join('\n', content.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
            if (path == "configuration.yaml")
            {
                content = ReplaceExact(content, "  internal_url: \"http://192.168.3.53:8123\"", "  internal_url: " + Scalar("http://" + settings.NasAddress + ":8123"), 1);
                content = ReplaceExact(content, "    custom_components.yandex_smart_home: info\n", "", 1);
            }
            if (path is "packages/docker.yaml" or "packages/monitoring.yaml")
                content = ReplaceExact(content, "script.pachca_notify", "script.hh_homelab_notify", path == "packages/docker.yaml" ? 5 : 6);
            if (path == "packages/media_tv.yaml")
                content = ReplaceExact(content, "broadcast_address: \"192.168.3.255\"", "broadcast_address: " + Scalar(settings.TvBroadcastAddress), 2);
            if (path == "bin/http_check.py")
                content = ReplaceExact(content,
                    "    # Самоподписанные сертификаты DSM не должны валить проверку\n    ctx = ssl.create_default_context()\n    ctx.check_hostname = False\n    ctx.verify_mode = ssl.CERT_NONE",
                    "    # HTTPS requires the system trust store; never bypass certificate verification.\n    ctx = ssl.create_default_context()", 1);
            files.Add(path, Encoding.UTF8.GetBytes(content));
        }
        files.Add("packages/local_notifications.yaml", Encoding.UTF8.GetBytes(LocalNotifications));
        var secrets = "home_latitude: " + settings.Latitude.ToString("R", CultureInfo.InvariantCulture) + "\n" +
            "home_longitude: " + settings.Longitude.ToString("R", CultureInfo.InvariantCulture) + "\n" +
            "home_elevation: " + settings.Elevation.ToString(CultureInfo.InvariantCulture) + "\n" +
            "tv_lg_mac: " + Scalar(settings.TvMac) + "\n" +
            "jellyfin_user_id: " + Scalar(settings.JellyfinUserId) + "\n" +
            "jellyfin_auth_header: " + Scalar("MediaBrowser Token=\"" + settings.JellyfinToken + "\"") + "\n";
        files.Add("secrets.yaml", Encoding.UTF8.GetBytes(secrets));
        foreach (var (path, bytes) in component.Files) files.Add(path, bytes);
        return new HomeAssistantPayload(files);
    }

    internal static void Validate(HomeAssistantLocalSettings settings)
    {
        if (!Ipv4(settings.NasAddress) || !Ipv4(settings.TvBroadcastAddress) ||
            !MacPattern().IsMatch(settings.TvMac ?? "") || !HexIdPattern().IsMatch(settings.JellyfinUserId ?? "") ||
            !TokenPattern().IsMatch(settings.JellyfinToken ?? "") || !double.IsFinite(settings.Latitude) ||
            !double.IsFinite(settings.Longitude) || settings.Latitude is < -90 or > 90 || settings.Longitude is < -180 or > 180 ||
            settings.Elevation is < -500 or > 9000)
            throw new ArgumentException("ha.profile.settings.invalid");
    }

    private static bool Ipv4(string value) => value is not null && IPAddress.TryParse(value, out var address) &&
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && address.ToString() == value;
    private static string Scalar(string value) => JsonSerializer.Serialize(value);
    private static string ReplaceExact(string content, string oldValue, string newValue, int count)
    {
        if (content.Split(oldValue, StringSplitOptions.None).Length - 1 != count)
            throw new InvalidDataException("ha.profile.transform.anchor.invalid");
        return content.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private const string LocalNotifications = """
        script:
          hh_homelab_notify:
            alias: "hh-homelab: локальное уведомление"
            mode: queued
            max: 20
            fields:
              title:
                description: "Заголовок"
              text:
                description: "Сообщение"
              level:
                description: "Уровень"
            sequence:
              - action: persistent_notification.create
                data:
                  title: "{{ title | default('hh-homelab', true) }}"
                  message: "{{ text | default('', true) }}"

        """;
    [GeneratedRegex(@"\A(?:[0-9a-fA-F]{2}:){5}[0-9a-fA-F]{2}\z", RegexOptions.CultureInvariant)]
    private static partial Regex MacPattern();
    [GeneratedRegex(@"\A[0-9a-fA-F]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex HexIdPattern();
    [GeneratedRegex(@"\A[A-Za-z0-9_-]{16,256}\z", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
