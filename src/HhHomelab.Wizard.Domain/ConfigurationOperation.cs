using System.Security.Cryptography;
using System.Text.Json;

namespace HhHomelab.Wizard.Domain;

public enum ConfigurationOperationKind
{
    QbittorrentCategory,
    ArrRootFolder,
}

/// <summary>Opaque identities supplied by the ownership/journal boundary, never credentials or URLs.
/// A well-formed identity alone does not prove ownership of a remote service.</summary>
public sealed record ResourceIdentity(Guid InstallationId, Guid ResourceId, string Service, string ProfileFingerprint, string EndpointFingerprint)
{
    public bool IsValid => InstallationId != Guid.Empty
        && ResourceId != Guid.Empty
        && Service is "qbittorrent" or "radarr" or "sonarr"
        && IsFingerprint(ProfileFingerprint)
        && IsFingerprint(EndpointFingerprint);

    public static bool IsFingerprint(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

public sealed record OperationIdentity(Guid OperationId, ResourceIdentity Resource);

public sealed record OperationDescriptor(OperationIdentity Identity, ConfigurationOperationKind Kind, string SpecificationHash)
{
    public bool IsValid => Identity is { OperationId: var id, Resource: { IsValid: true } }
        && id != Guid.Empty
        && Enum.IsDefined(Kind)
        && ResourceIdentity.IsFingerprint(SpecificationHash);
}

/// <summary>Two bounded sample operations. Does not authorize deployment or contain credentials.</summary>
public sealed record ConfigurationRequest(OperationIdentity Identity, ConfigurationOperationKind Kind, string Name, string Path)
{
    public OperationDescriptor Descriptor => new(Identity, Kind, Fingerprint(new { Kind, Name, Path = Path?.TrimEnd('/') }));

    public bool IsValid()
    {
        if (!Descriptor.IsValid || !QbittorrentDownloadSettings.IsDataPath(Path))
            return false;
        if (Kind == ConfigurationOperationKind.ArrRootFolder)
            return Identity.Resource.Service is "radarr" or "sonarr"
                && Name == "root"
                && Path.StartsWith("/data/media/", StringComparison.Ordinal);
        if (Identity.Resource.Service != "qbittorrent" || string.IsNullOrEmpty(Name))
            return false;
        return Name.Length <= 64 && Name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    }

    public static string Fingerprint<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
}

public enum ObservedResourceState
{
    Missing,
    Satisfied,
    Conflict,
}

public sealed record OperationObservation(OperationDescriptor Descriptor, ObservedResourceState State, string Revision)
{
    public bool IsValidFor(OperationDescriptor expected) => Descriptor == expected
        && Enum.IsDefined(State)
        && ResourceIdentity.IsFingerprint(Revision);
}

/// <summary>Independent read after Apply, or a second read before confirming an existing result.</summary>
public sealed record OperationVerification(OperationDescriptor Descriptor, ObservedResourceState State, string Revision)
{
    public bool IsSatisfiedFor(OperationDescriptor expected) => Descriptor == expected
        && State == ObservedResourceState.Satisfied
        && ResourceIdentity.IsFingerprint(Revision);
}
