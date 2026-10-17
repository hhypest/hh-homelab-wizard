namespace HhHomelab.Wizard.Domain;

/// <summary>Non-secret library identity used to reconcile Jellyfin and Seerr.</summary>
public sealed record MediaLibrary(string Name, string CollectionType, string Path)
{
    public bool IsValid() =>
        !string.IsNullOrWhiteSpace(Name)
        && Name == Name.Trim()
        && !Name.Any(char.IsControl)
        && CollectionType is "movies" or "tvshows"
        && QbittorrentDownloadSettings.IsDataPath(Path)
        && Path.StartsWith("/data/media/", StringComparison.Ordinal);
}

public sealed record JellyfinLibraryIdentity(string Id, string Name, string CollectionType);

public sealed record MediaQualityProfile(int Id, string Name);

/// <summary>User-selected Arr settings. Never select a profile by its position or assume an ID.</summary>
public sealed record SeerrArrConnection(
    ArrService Service,
    Uri Endpoint,
    int ProfileId,
    string ProfileName,
    string RootFolder,
    bool PreventSearch = true
)
{
    public bool IsValid() =>
        Service is ArrService.Radarr or ArrService.Sonarr
        && ProfileId > 0
        && !string.IsNullOrWhiteSpace(ProfileName)
        && !ProfileName.Any(char.IsControl)
        && QbittorrentDownloadSettings.IsDataPath(RootFolder)
        && RootFolder.StartsWith("/data/media/", StringComparison.Ordinal);
}
