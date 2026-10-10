using System.Text.RegularExpressions;

namespace HhHomelab.Wizard.Domain;

/// <summary>A research inventory, not a deployable installation plan.</summary>
public sealed record ResearchProfile(
    int SchemaVersion,
    string Id,
    SourceReference Source,
    SourceArtifact[] Artifacts,
    ServiceContract[] Services,
    ComponentReference[] Components,
    CoverageStep[] Coverage);

public sealed record SourceReference(string Repository, string Tag, string Commit, string HashMode);
public sealed record SourceArtifact(string Path, string Sha256);
public sealed record ServiceContract(string Id, string Image, string[] DockerMods, string ApiFamily, string Evidence);
public sealed record ComponentReference(string Id, string Repository, string Tag, string Commit, string ManifestVersion, string License);
public sealed record CoverageStep(string Id, string SourcePath, string Title, string Mode, string Operation, string Status);

public static partial class ResearchProfileValidator
{
    private static readonly HashSet<string> Modes = ["Automatic", "Interactive", "Guidance", "Optional", "Excluded", "Deferred"];
    private static readonly HashSet<string> Evidence = ["SourceReviewed", "DocumentationReviewed", "RuntimePartial", "Unverified"];

    /// <summary>Rejects malformed provenance and coverage before it can be used by tooling.</summary>
    public static IReadOnlyList<string> Validate(ResearchProfile profile)
    {
        var errors = new List<string>();
        if (profile.SchemaVersion != 1) errors.Add("schema.unsupported");
        if (string.IsNullOrWhiteSpace(profile.Id)) errors.Add("profile.id.required");
        if (profile.Source is null ||
            profile.Source.Repository != "https://github.com/hhypest/hh-homelab" ||
            !IsCommit(profile.Source.Commit) || string.IsNullOrWhiteSpace(profile.Source.Tag) ||
            profile.Source.HashMode != "utf8-lf") errors.Add("source.invalid");

        if (profile.Artifacts is not { Length: > 0 }) errors.Add("artifacts.required");
        else
        {
            Unique(profile.Artifacts.Select(a => a?.Path), "artifact", errors);
            foreach (var artifact in profile.Artifacts)
                if (artifact is null || !IsRelativePath(artifact.Path) ||
                    !Sha256Pattern().IsMatch(artifact.Sha256 ?? "")) errors.Add("artifact.invalid");
        }

        if (profile.Services is not { Length: > 0 }) errors.Add("services.required");
        else
        {
            Unique(profile.Services.Select(s => s?.Id), "service", errors);
            foreach (var service in profile.Services)
                if (service is null || !IsPinnedImage(service.Image) || service.DockerMods is null ||
                    service.DockerMods.Any(m => !IsPinnedImage(m)) ||
                    string.IsNullOrWhiteSpace(service.ApiFamily) || !Evidence.Contains(service.Evidence ?? ""))
                    errors.Add("service.invalid");
        }

        if (profile.Components is null) errors.Add("components.required");
        else
        {
            Unique(profile.Components.Select(c => c?.Id), "component", errors);
            foreach (var component in profile.Components)
                if (component is null || !IsCommit(component.Commit) ||
                    !Uri.TryCreate(component.Repository, UriKind.Absolute, out var repository) || repository.Scheme != "https" ||
                    string.IsNullOrWhiteSpace(component.Tag) ||
                    string.IsNullOrWhiteSpace(component.ManifestVersion) || string.IsNullOrWhiteSpace(component.License))
                    errors.Add("component.invalid");
        }

        if (profile.Coverage is not { Length: > 0 }) errors.Add("coverage.required");
        else
        {
            Unique(profile.Coverage.Select(s => s?.Id), "step", errors);
            var artifactPaths = (profile.Artifacts ?? []).Where(a => a is not null).Select(a => a.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var step in profile.Coverage)
                if (step is null || !artifactPaths.Contains(step.SourcePath ?? "") ||
                    !Modes.Contains(step.Mode ?? "") || string.IsNullOrWhiteSpace(step.Title) ||
                    string.IsNullOrWhiteSpace(step.Operation) || step.Status != "NotImplemented")
                    errors.Add("step.invalid");
        }
        return errors;
    }

    public static bool IsRelativePath(string? path) => !string.IsNullOrEmpty(path) &&
        !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') &&
        path.Split('/').All(p => p is not ("" or "." or ".."));

    public static bool IsPinnedImage(string? image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.Any(char.IsWhiteSpace) || image.Contains('@')) return false;
        var separator = image.LastIndexOf(':');
        if (separator <= image.LastIndexOf('/') || separator == image.Length - 1) return false;
        var tag = image[(separator + 1)..];
        return TagPattern().IsMatch(tag) && tag.Any(char.IsDigit);
    }

    private static bool IsCommit(string? commit) => CommitPattern().IsMatch(commit ?? "");
    private static void Unique(IEnumerable<string?> values, string kind, List<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (string.IsNullOrWhiteSpace(value) || !seen.Add(value)) errors.Add(kind + ".id.invalid");
    }

    [GeneratedRegex("^[a-f0-9]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();
    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Pattern();
    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
}
