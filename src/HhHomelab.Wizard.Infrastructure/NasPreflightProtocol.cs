using System.Globalization;
using System.Text;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

internal static class NasPreflightProtocol
{
    private static readonly Lazy<string> EmbeddedCommand = new(ReadCommand);

    internal static string Command => EmbeddedCommand.Value;

    private static string ReadCommand()
    {
        using var stream = typeof(NasPreflightProtocol).Assembly.GetManifestResourceStream("nas-preflight.sh")
            ?? throw new InvalidDataException("preflight.command.missing");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd().TrimEnd('\n');
    }

    internal static NasPreflightFacts Parse(int? exitCode, string output)
    {
        if (exitCode != 0 || output.Length > SshProbeOutput.MaximumBytes)
            throw Invalid();
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        if (lines.Length is < 10 or > 138)
            throw Invalid();
        if (lines[0] != "hh-homelab-wizard:nas-preflight:v1" || lines[^1] != "end")
            throw Invalid();
        var kernel = Field(lines[1], "kernel");
        var architecture = Field(lines[2], "architecture");
        var time = Field(lines[3], "unix-time");
        if (!long.TryParse(time, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            throw Invalid();
        var inventory = Field(lines[8], "containers") switch
        {
            "available" => PreflightFactStatus.Available,
            "unavailable" => PreflightFactStatus.Unavailable,
            _ => throw Invalid(),
        };
        try
        {
            return new(kernel, architecture, DateTimeOffset.FromUnixTimeSeconds(seconds),
                Version(Field(lines[4], "dsm")), Version(Field(lines[5], "container-manager")),
                Version(Field(lines[6], "docker")), Version(Field(lines[7], "compose")),
                inventory, lines[9..^1].Select(line => Field(line, "container")));
        }
        catch (ArgumentException)
        {
            throw Invalid();
        }
    }

    private static string Field(string line, string name)
    {
        var prefix = name + "=";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            throw Invalid();
        return line[prefix.Length..];
    }

    private static PreflightVersion Version(string value) => value switch
    {
        "missing" => new(PreflightFactStatus.Missing),
        "unavailable" => new(PreflightFactStatus.Unavailable),
        _ when value.StartsWith("available:", StringComparison.Ordinal) => new(PreflightFactStatus.Available, value[10..]),
        _ => throw Invalid(),
    };

    private static InvalidDataException Invalid() => new("preflight.response.invalid");
}
