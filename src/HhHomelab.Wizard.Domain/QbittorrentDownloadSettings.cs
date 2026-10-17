namespace HhHomelab.Wizard.Domain;

/// <summary>Only the download settings owned by the media profile; no credentials.</summary>
public sealed record QbittorrentDownloadSettings(
    string SavePath,
    string TemporaryPath,
    bool TemporaryPathEnabled
)
{
    public bool IsValid() => IsDataPath(SavePath) && IsDataPath(TemporaryPath);

    public static bool IsDataPath(string? path) =>
        path is not null
        && (path == "/data" || path.StartsWith("/data/", StringComparison.Ordinal))
        && !path.Any(char.IsControl)
        && !path.Contains('\\')
        && !path.Contains("//", StringComparison.Ordinal)
        && path.TrimEnd('/').Split('/').Skip(1).All(p => p is not ("" or "." or ".."));

    public bool Matches(QbittorrentDownloadSettings other) =>
        SavePath.TrimEnd('/') == other.SavePath.TrimEnd('/')
        && TemporaryPath.TrimEnd('/') == other.TemporaryPath.TrimEnd('/')
        && TemporaryPathEnabled == other.TemporaryPathEnabled;
}
