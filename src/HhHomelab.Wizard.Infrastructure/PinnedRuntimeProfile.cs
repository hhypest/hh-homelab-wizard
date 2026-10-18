using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>A build-time snapshot. No external profile or later file edit can select new contracts.
/// Compatibility proves equality with this build, not provenance of a checkout or API support.</summary>
public static class PinnedRuntimeProfile
{
    private static readonly ResearchProfile Snapshot = ReadSnapshot();

    private static readonly FrozenDictionary<string, PinnedService> Services = Snapshot.Services
        .ToFrozenDictionary(service => service.Id, CreateService, StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, ComponentReference> Components = Snapshot.Components
        .ToFrozenDictionary(component => component.Id, StringComparer.Ordinal);

    public static string Fingerprint { get; } = Hash(Snapshot);

    public static SourceReference Source => Snapshot.Source;

    public static PinnedService Service(string id) => Services.TryGetValue(id, out var service)
        ? service
        : throw new InvalidDataException("profile.service.unsupported");

    public static ComponentReference Component(string id) => Components.TryGetValue(id, out var component)
        ? component
        : throw new InvalidDataException("profile.component.unsupported");

    public static string Version(string id) => Service(id).RuntimeVersion
        ?? throw new InvalidDataException("profile.runtime-version.unsupported");

    /// <summary>Rejects drift in the entire parsed profile before a probe contacts Docker/HTTP.
    /// Whitespace is immaterial; array order and all modeled fields are significant.</summary>
    public static void EnsureCompatible(ResearchProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (ResearchProfileValidator.Validate(profile).Count != 0)
            throw new InvalidDataException("profile.runtime.incompatible");
        if (Hash(profile) != Fingerprint)
            throw new InvalidDataException("profile.runtime.incompatible");
    }

    private static PinnedService CreateService(ServiceContract service) => new(
        service.Image,
        service.ApiFamily,
        service.RuntimeVersion,
        Array.AsReadOnly(service.DockerMods.ToArray())
    );

    private static ResearchProfile ReadSnapshot()
    {
        using var stream = typeof(PinnedRuntimeProfile).Assembly
            .GetManifestResourceStream("ha-source-profile.json")
            ?? throw new InvalidDataException("profile.runtime.missing");
        using var reader = new StreamReader(stream);
        var profile = ProfileReader.Parse(reader.ReadToEnd());
        if (profile.SchemaVersion != 2)
            throw new InvalidDataException("profile.runtime.schema-unsupported");
        return profile;
    }

    private static string Hash(ResearchProfile profile) => Convert.ToHexStringLower(
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile))
    );
}

/// <summary>Immutable service values; the Docker-mod collection cannot mutate the snapshot.</summary>
public sealed record PinnedService(
    string Image,
    string ApiFamily,
    string? RuntimeVersion,
    IReadOnlyList<string> DockerMods
);
