namespace HhHomelab.Wizard.Domain;

/// <summary>Build-time layout contract plus the existing research pins. Separate from credentials,
/// user choices and observed state. A template change changes the concrete plan fingerprint.</summary>
public sealed class QbittorrentPlanProfile
{
    public QbittorrentPlanProfile(string researchFingerprint, string sourceCommit, string composeSha256,
        string image, IReadOnlyList<string> dockerMods, string composePath, string environmentPath,
        string configurationPath, string watchPath, string containerName, int containerWebUiPort)
    {
        if (!ResourceIdentity.IsFingerprint(researchFingerprint) || sourceCommit is not { Length: 40 }
            || !sourceCommit.All(char.IsAsciiHexDigit) || !ResourceIdentity.IsFingerprint(composeSha256))
            throw new ArgumentException("plan.profile.source");
        if (!ResearchProfileValidator.IsPinnedImage(image) || dockerMods is null
            || dockerMods.Count > 16 || dockerMods.Any(mod => !ResearchProfileValidator.IsPinnedImage(mod)))
            throw new ArgumentException("plan.profile.images");
        var paths = new[] { composePath, environmentPath, configurationPath, watchPath };
        if (paths.Any(path => !ResearchProfileValidator.IsRelativePath(path)) || paths.Distinct().Count() != paths.Length
            || containerName != "qbittorrent" || containerWebUiPort is < 1 or > 65535)
            throw new ArgumentException("plan.profile.layout");
        ResearchFingerprint = researchFingerprint;
        SourceCommit = sourceCommit;
        ComposeSha256 = composeSha256;
        Image = image;
        DockerMods = Array.AsReadOnly(dockerMods.ToArray());
        ComposePath = composePath;
        EnvironmentPath = environmentPath;
        ConfigurationPath = configurationPath;
        WatchPath = watchPath;
        ContainerName = containerName;
        ContainerWebUiPort = containerWebUiPort;
        Fingerprint = ConfigurationRequest.Fingerprint(new
        {
            researchFingerprint, sourceCommit, composeSha256, image, DockerMods,
            composePath, environmentPath, configurationPath, watchPath, containerName, containerWebUiPort,
        });
    }

    public string ResearchFingerprint { get; }
    public string SourceCommit { get; }
    public string ComposeSha256 { get; }
    public string Image { get; }
    public IReadOnlyList<string> DockerMods { get; }
    public string ComposePath { get; }
    public string EnvironmentPath { get; }
    public string ConfigurationPath { get; }
    public string WatchPath { get; }
    public string ContainerName { get; }
    public int ContainerWebUiPort { get; }
    public string Fingerprint { get; }
}
