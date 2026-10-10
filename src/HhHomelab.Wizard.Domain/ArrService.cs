namespace HhHomelab.Wizard.Domain;

public enum ArrService { Radarr, Sonarr, Prowlarr }

/// <summary>Connection parameters only; credentials are passed separately and never recorded here.</summary>
public sealed record ArrDownloadConnection(string Host, int Port, string Category)
{
    public bool IsValid() => !string.IsNullOrWhiteSpace(Host) &&
        Host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-') &&
        Port is > 0 and <= 65535 && Category is "movies" or "tv";
}
