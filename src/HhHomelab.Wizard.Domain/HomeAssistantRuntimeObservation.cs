namespace HhHomelab.Wizard.Domain;

public enum HomeAssistantValueStatus
{
    Missing,
    Unknown,
    Unavailable,
    Invalid,
    Available,
}

/// <summary>Restricted observation: no raw attributes, names, credentials or media titles.</summary>
public sealed record HomeAssistantValueObservation(
    string EntityId,
    HomeAssistantValueStatus Status,
    double? Number = null
);

/// <summary>Observations are not overall installation readiness; hardware may still be absent.</summary>
public sealed record HomeAssistantRuntimeObservation(
    bool MonitorDockerLoaded,
    IReadOnlyList<HomeAssistantValueObservation> DockerValues,
    IReadOnlyList<HomeAssistantValueObservation> Hardware,
    IReadOnlyList<HomeAssistantValueObservation> LocalScripts,
    bool? DockerProxyError,
    bool? DockerOom,
    int? DockerRestarting
);
