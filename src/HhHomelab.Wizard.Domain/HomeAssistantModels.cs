using System.Text.RegularExpressions;

namespace HhHomelab.Wizard.Domain;

/// <summary>Observed identity, not a name-based selection of a device or metric.</summary>
public sealed record HomeAssistantEntity(string RegistryId, string EntityId, string UniqueId, string Platform,
    string? ConfigEntryId, string? DeviceId, string? DisabledBy);

public enum HomeAssistantHttpStage { Unchanged, RestartRequired, AwaitingVerification }

public enum HomeAssistantJellyfinStage
{ AwaitingCredentials, AuthenticationRejected, CannotConnect, AwaitingSetup, Loaded, OutcomeUnknown }

/// <summary>Safe progress only. Credentials and raw config-flow responses are never returned.</summary>
public sealed record HomeAssistantJellyfinProgress(HomeAssistantJellyfinStage Stage, string? EntryId);

/// <summary>M0 storage dashboard contract. Full profile YAML rendering is a separate operation.</summary>
public sealed record HomeAssistantDashboard(string UrlPath, string Title, IReadOnlyList<string> Entities)
{
    public bool IsValid() => Regex.IsMatch(UrlPath ?? "", @"\Ahh-homelab(?:-[a-z0-9]+)*\z", RegexOptions.CultureInvariant) &&
        !string.IsNullOrWhiteSpace(Title) && !Title.Any(char.IsControl) && Entities is { Count: > 0 and <= 100 } &&
        Entities.All(IsEntityId) && Entities.Distinct(StringComparer.Ordinal).Count() == Entities.Count;

    public static bool IsEntityId(string? value) => value is not null &&
        Regex.IsMatch(value, @"\A[a-z_]+\.[a-z0-9_]+\z", RegexOptions.CultureInvariant);
}
