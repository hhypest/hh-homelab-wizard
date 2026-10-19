using System.Text.Json;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

/// <summary>The rendered qBittorrent Compose and .env, parsed and interpolated by the pinned Compose of the
/// disposable Docker fixture (`docker compose config`). Nothing is pulled, created or started; the NAS of the
/// owner is never involved. Proves syntax, .env interpolation and the normalized service, not runtime.</summary>
internal static class ComposeConfigScenarios
{
    private const string DataRoot = "/volume1/data";

    internal static async Task RunAsync(OwnedDockerBench bench, CancellationToken token)
    {
        var profile = new PinnedQbittorrentPlanProfile().Definition;
        var request = new QbittorrentInstallationRequest(Guid.Parse("b3d64c85-e391-4960-b58a-c157d74acb62"), bench.SshTarget,
            new MediaPathLayout(DataRoot, "/volume1/apps/hhw"), "192.0.2.53", "0.0.0.0", 19080, 16881, "media-test",
            1026, 100, "002", "Europe/Moscow", 1024, 512);
        var files = Copy(QbittorrentConfigurationRenderer.Render(request, profile));
        var project = ProjectDirectoryOf(profile.ComposePath);

        var first = await bench.RunComposeConfigAsync(files, project, token);
        Require(first.ExitCode == 0, "config.exit");
        VerifyNormalized(first.Output, request, profile, project);
        var second = await bench.RunComposeConfigAsync(files, project, token);
        Require(second.ExitCode == 0 && second.Output == first.Output, "config.deterministic");
        Console.WriteLine("compose-config.rendered-qbittorrent: passed");

        // Without .env every required variable stops Compose with the rendered hint instead of an empty value.
        var withoutEnvironment = files.Where(file => file.RelativePath != profile.EnvironmentPath).ToList();
        var missing = await bench.RunComposeConfigAsync(withoutEnvironment, project, token);
        Require(missing.ExitCode != 0 && missing.Output.Length == 0, "config.missing-env.exit");
        Require(missing.Error.Contains("см. .env", StringComparison.Ordinal), "config.missing-env.hint");
        Console.WriteLine("compose-config.missing-env-refused: passed");
    }

    private static void VerifyNormalized(string output, QbittorrentInstallationRequest request, QbittorrentPlanProfile profile,
        string project)
    {
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        Require(root.GetProperty("name").GetString() == "hhw-" + request.InstallationId.ToString("N"), "config.project-name");
        var services = root.GetProperty("services");
        Require(services.EnumerateObject().Select(service => service.Name).SequenceEqual([profile.ContainerName]), "config.services");
        var service = services.GetProperty(profile.ContainerName);
        Require(service.GetProperty("image").GetString() == profile.Image, "config.image");
        Require(service.GetProperty("container_name").GetString() == profile.ContainerName, "config.container-name");
        Require(service.GetProperty("restart").GetString() == "unless-stopped", "config.restart");

        var environment = service.GetProperty("environment").EnumerateObject()
            .ToDictionary(variable => variable.Name, variable => variable.Value.GetString(), StringComparer.Ordinal);
        var expected = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PUID"] = "1026",
            ["PGID"] = "100",
            ["UMASK"] = "002",
            ["TZ"] = "Europe/Moscow",
            ["WEBUI_PORT"] = "19080",
            ["TORRENTING_PORT"] = "16881",
        };
        if (profile.DockerMods.Count > 0)
            expected["DOCKER_MODS"] = string.Join('|', profile.DockerMods);
        Require(environment.Count == expected.Count && expected.All(pair => environment.TryGetValue(pair.Key, out var value) && value == pair.Value),
            "config.environment");

        var directory = OwnedDockerBench.ComposeWorkPath(project);
        var volumes = service.GetProperty("volumes").EnumerateArray()
            .Select(volume => (Type: volume.GetProperty("type").GetString(), Source: volume.GetProperty("source").GetString(),
                Target: volume.GetProperty("target").GetString()))
            .ToList();
        Require(volumes.SequenceEqual(
        [
            ("bind", OwnedDockerBench.ComposeWorkPath(profile.ConfigurationPath), "/config"),
            ("bind", OwnedDockerBench.ComposeWorkPath(profile.WatchPath), "/watch"),
            ("bind", DataRoot, "/data"),
        ]), "config.volumes");
        Require(volumes.Take(2).All(volume => volume.Source!.StartsWith(directory + "/", StringComparison.Ordinal)), "config.volumes.relative");

        var ports = service.GetProperty("ports").EnumerateArray()
            .Select(port => (HostIp: port.GetProperty("host_ip").GetString(), Published: Text(port.GetProperty("published")),
                Target: port.GetProperty("target").GetInt32(), Protocol: port.GetProperty("protocol").GetString()))
            .ToList();
        Require(ports.SequenceEqual(
        [
            ("0.0.0.0", "19080", 19080, "tcp"),
            ("0.0.0.0", "16881", 16881, "tcp"),
            ("0.0.0.0", "16881", 16881, "udp"),
        ]), "config.ports");

        Require(service.GetProperty("networks").EnumerateObject().Select(network => network.Name).SequenceEqual(["media"]), "config.service-networks");
        var network = root.GetProperty("networks").GetProperty("media");
        Require(network.GetProperty("name").GetString() == "media-test", "config.network-name");
        Require(network.GetProperty("driver").GetString() == "bridge", "config.network-driver");
        var logging = service.GetProperty("logging");
        Require(logging.GetProperty("driver").GetString() == "json-file", "config.logging");
        Require(logging.GetProperty("options").GetProperty("max-size").GetString() == "10m", "config.logging.size");
        Require(logging.GetProperty("options").GetProperty("max-file").GetString() == "3", "config.logging.files");
    }

    /// <summary>Compose reports a published port as a string or a number depending on the version.</summary>
    private static string? Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number ? value.GetRawText() : value.GetString();

    private static List<(string RelativePath, byte[] Content)> Copy(IReadOnlyList<RenderedConfigurationFile> rendered)
    {
        try
        {
            return rendered.Select(file => (file.RelativePath, file.Content.ToArray())).ToList();
        }
        finally
        {
            foreach (var file in rendered)
                file.Dispose();
        }
    }

    private static string ProjectDirectoryOf(string relativePath) => relativePath[..relativePath.LastIndexOf('/')];

    private static void Require(bool satisfied, string code)
    {
        if (!satisfied)
            throw new SshScenarioException("compose-config." + code);
    }
}
