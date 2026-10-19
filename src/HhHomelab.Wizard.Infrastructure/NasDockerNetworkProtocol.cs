using System.Text;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

internal static class NasDockerNetworkProtocol
{
    internal const string RequestMarker = "hh-homelab-wizard:nas-network-request:v1";

    internal const string ResponseMarker = "hh-homelab-wizard:nas-network:v1";

    private static readonly Lazy<string> EmbeddedCommand = new(ReadCommand);

    internal static string Command => EmbeddedCommand.Value;

    private static string ReadCommand()
    {
        using var stream = typeof(NasDockerNetworkProtocol).Assembly.GetManifestResourceStream("nas-network-read.sh")
            ?? throw new InvalidDataException("network.command.missing");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd().TrimEnd('\n');
    }

    internal static string Request(string networkName)
    {
        if (!DockerNetworkSnapshot.IsValidName(networkName))
            throw new ArgumentException("network.request.invalid");

        return RequestMarker + "\nnetwork=" + networkName + "\nend\n";
    }

    internal static DockerNetworkFacts Parse(int? exitCode, string output)
    {
        if (exitCode != 0 || output.Length > SshProbeOutput.MaximumBytes)
            throw Invalid();

        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        if (lines.Length is < 3 or > DockerNetworkFacts.MaximumMatches + 3)
            throw Invalid();
        if (lines[0] != ResponseMarker || lines[^1] != "end")
            throw Invalid();

        var status = lines[^2] switch
        {
            "status=available" => PreflightFactStatus.Available,
            "status=unavailable" => PreflightFactStatus.Unavailable,
            _ => throw Invalid(),
        };
        var ids = new List<string>();
        foreach (var line in lines[1..^2])
        {
            if (line == "skip")
                continue;
            if (!line.StartsWith("network=", StringComparison.Ordinal))
                throw Invalid();

            ids.Add(line[8..]);
        }
        try
        {
            return new(status, ids);
        }
        catch (ArgumentException)
        {
            throw Invalid();
        }
    }

    private static InvalidDataException Invalid() => new("network.response.invalid");
}
