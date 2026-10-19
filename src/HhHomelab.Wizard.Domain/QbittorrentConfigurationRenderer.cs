using System.Globalization;
using System.Text;

namespace HhHomelab.Wizard.Domain;

/// <summary>One generated file below the configuration root: path, permission bits and exact bytes.</summary>
public sealed class RenderedConfigurationFile
{
    private readonly byte[] content;

    internal RenderedConfigurationFile(string relativePath, int mode, byte[] content)
    {
        RelativePath = relativePath;
        Mode = mode;
        this.content = content;
    }

    public string RelativePath { get; }

    public int Mode { get; }

    public ReadOnlySpan<byte> Content => content;
}

/// <summary>Deterministic Compose and .env text for the qBittorrent service of a concrete plan. The service
/// follows the pinned upstream definition (image, mods, mounts, publications, network, logging); the only
/// intended difference is the aligned WebUI port of the plan's host-and-container policy. Values live in
/// .env; nothing is fetched or evaluated, and no secret is written.</summary>
public static class QbittorrentConfigurationRenderer
{
    public const int ComposeMode = 0x1A4;

    public const int EnvironmentMode = 0x180;

    public static IReadOnlyList<RenderedConfigurationFile> Render(QbittorrentInstallationPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var profile = plan.Profile;
        var directory = DirectoryOf(profile.ComposePath);
        // Compose reads .env and resolves ./ mounts from its own directory.
        if (DirectoryOf(profile.EnvironmentPath) != directory)
            throw new ArgumentException("render.layout.environment");
        var configuration = Below(directory, profile.ConfigurationPath);
        var watch = Below(directory, profile.WatchPath);
        var dataRoot = plan.Request.Layout.DataRoot;
        // A single-quoted .env value is literal; a colon would split the short volume syntax.
        if (dataRoot.Contains('\'', StringComparison.Ordinal) || dataRoot.Contains(':', StringComparison.Ordinal))
            throw new ArgumentException("render.data-root.unsupported");
        var compose = Compose(plan, configuration, watch);
        var environment = Environment(plan, dataRoot);
        return Array.AsReadOnly(new[]
        {
            new RenderedConfigurationFile(profile.ComposePath, ComposeMode, Utf8(compose)),
            new RenderedConfigurationFile(profile.EnvironmentPath, EnvironmentMode, Utf8(environment)),
        });
    }

    private static string Compose(QbittorrentInstallationPlan plan, string configuration, string watch)
    {
        var profile = plan.Profile;
        var text = new StringBuilder();
        text.Append("# Создано hh-homelab-wizard для установки ").Append(plan.Owner.InstallationId.ToString("D")).Append('\n');
        text.Append("# Источник: hh-homelab ").Append(profile.SourceCommit).Append(", media/compose.yaml (sha256 ")
            .Append(profile.ComposeSha256).Append("), сервис qbittorrent.\n");
        text.Append("# Значения — в .env рядом с этим файлом. Мастер не перезаписывает ручные изменения.\n");
        text.Append("services:\n");
        text.Append("  ").Append(profile.ContainerName).Append(":\n");
        text.Append("    image: ").Append(profile.Image).Append('\n');
        text.Append("    container_name: ").Append(profile.ContainerName).Append('\n');
        text.Append("    restart: unless-stopped\n");
        text.Append("    environment:\n");
        text.Append("      - PUID=${PUID:?см. .env}\n");
        text.Append("      - PGID=${PGID:?см. .env}\n");
        text.Append("      - UMASK=${UMASK:?см. .env}\n");
        text.Append("      - TZ=${TZ:?см. .env}\n");
        // host-and-container: the protected WebUI rejects a Host port that differs from WEBUI_PORT.
        text.Append("      - WEBUI_PORT=${QBITTORRENT_WEBUI_PORT:?см. .env}\n");
        text.Append("      - TORRENTING_PORT=${QBITTORRENT_TORRENT_PORT:?см. .env}\n");
        if (profile.DockerMods.Count > 0)
            text.Append("      - DOCKER_MODS=").Append(string.Join('|', profile.DockerMods)).Append('\n');
        text.Append("    volumes:\n");
        text.Append("      - ./").Append(configuration).Append(":/config\n");
        text.Append("      - ./").Append(watch).Append(":/watch\n");
        text.Append("      - ${MEDIA_ROOT:?см. .env}:/data\n");
        text.Append("    ports:\n");
        text.Append("      - \"${BIND_ADDR:?см. .env}:${QBITTORRENT_WEBUI_PORT}:${QBITTORRENT_WEBUI_PORT}\"\n");
        text.Append("      - \"${BIND_ADDR}:${QBITTORRENT_TORRENT_PORT}:${QBITTORRENT_TORRENT_PORT}\"\n");
        text.Append("      - \"${BIND_ADDR}:${QBITTORRENT_TORRENT_PORT}:${QBITTORRENT_TORRENT_PORT}/udp\"\n");
        text.Append("    networks:\n");
        text.Append("      - media\n");
        text.Append("    logging:\n");
        text.Append("      driver: json-file\n");
        text.Append("      options:\n");
        text.Append("        max-size: \"10m\"\n");
        text.Append("        max-file: \"3\"\n");
        text.Append('\n');
        text.Append("networks:\n");
        text.Append("  media:\n");
        text.Append("    name: ${MEDIA_NETWORK:?см. .env}\n");
        text.Append("    driver: bridge\n");
        return text.ToString();
    }

    private static string Environment(QbittorrentInstallationPlan plan, string dataRoot)
    {
        var request = plan.Request;
        var text = new StringBuilder();
        text.Append("# Создано hh-homelab-wizard для установки ").Append(plan.Owner.InstallationId.ToString("D")).Append('\n');
        text.Append("# Значения выбраны в мастере; секретов здесь нет.\n");
        text.Append("PUID=").Append(Number(request.UserId)).Append('\n');
        text.Append("PGID=").Append(Number(request.GroupId)).Append('\n');
        text.Append("UMASK=").Append(request.Umask).Append('\n');
        text.Append("TZ=").Append(request.TimeZone).Append('\n');
        text.Append("BIND_ADDR=").Append(request.BindAddress).Append('\n');
        text.Append("MEDIA_ROOT='").Append(dataRoot).Append("'\n");
        text.Append("MEDIA_NETWORK=").Append(request.NetworkName).Append('\n');
        text.Append("QBITTORRENT_WEBUI_PORT=").Append(Number(plan.ContainerWebUiPort)).Append('\n');
        text.Append("QBITTORRENT_TORRENT_PORT=").Append(Number(request.TorrentPort)).Append('\n');
        return text.ToString();
    }

    private static string DirectoryOf(string relativePath)
    {
        var separator = relativePath.LastIndexOf('/');
        if (separator <= 0)
            throw new ArgumentException("render.layout.directory");
        return relativePath[..separator];
    }

    private static string Below(string directory, string relativePath)
    {
        if (!relativePath.StartsWith(directory + "/", StringComparison.Ordinal))
            throw new ArgumentException("render.layout.mount");
        return relativePath[(directory.Length + 1)..];
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static byte[] Utf8(string text) => new UTF8Encoding(false, true).GetBytes(text);
}
