using System.Text.Json;
using System.Text.Json.Serialization;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Only the embedded, versioned layout is selectable; image/mod pins come from the existing
/// research snapshot. External files cannot replace the template or pins during a session.</summary>
public sealed class PinnedQbittorrentPlanProfile : IQbittorrentPlanProfile
{
    private static readonly QbittorrentPlanProfile Snapshot = Read();

    public QbittorrentPlanProfile Definition => Snapshot;

    private static QbittorrentPlanProfile Read()
    {
        using var stream = typeof(PinnedQbittorrentPlanProfile).Assembly.GetManifestResourceStream("qbittorrent-plan.json")
            ?? throw new InvalidDataException("plan.profile.missing");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    internal static QbittorrentPlanProfile Parse(string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true,
            AllowDuplicateProperties = false,
        };
        var data = JsonSerializer.Deserialize<Template>(json, options) ?? throw new InvalidDataException("plan.profile.invalid");
        if (data.Schema != 1 || data.SourceCommit != PinnedRuntimeProfile.Source.Commit
            || data.SourceComposeSha256 != PinnedRuntimeProfile.Artifact("media/compose.yaml").Sha256)
            throw new InvalidDataException("plan.profile.source");
        var service = PinnedRuntimeProfile.Service("qbittorrent");
        return new(PinnedRuntimeProfile.Fingerprint, data.SourceCommit, data.SourceComposeSha256,
            service.Image, service.DockerMods, data.ComposePath, data.EnvironmentPath,
            data.ConfigurationPath, data.WatchPath, data.ContainerName, data.WebUiPortPolicy);
    }

    private sealed record Template(int Schema, string SourceCommit, string SourceComposeSha256, string ComposePath,
        string EnvironmentPath, string ConfigurationPath, string WatchPath, string ContainerName, string WebUiPortPolicy);
}
