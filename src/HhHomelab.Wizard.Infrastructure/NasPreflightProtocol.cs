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

    internal const string Marker = "hh-homelab-wizard:nas-preflight:v2";

    internal static NasPreflightFacts Parse(int? exitCode, string output)
    {
        if (exitCode != 0 || output.Length > SshProbeOutput.MaximumBytes)
            throw Invalid();
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        if (lines.Length < 13)
            throw Invalid();
        if (lines[0] != Marker || lines[^1] != "end")
            throw Invalid();
        var kernel = Field(lines[1], "kernel");
        var architecture = Field(lines[2], "architecture");
        var time = Field(lines[3], "unix-time");
        if (!long.TryParse(time, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            throw Invalid();
        var next = 10;
        var inventory = Status(Field(lines[9], "containers"));
        var containers = Repeated(lines, ref next, "container", NasPreflightFacts.MaximumContainers);
        var listening = Status(Field(lines[next++], "listening"));
        var ports = Repeated(lines, ref next, "listen", NasHostResources.MaximumListeningPorts).Select(Port).ToArray();
        var volumeStatus = Status(Field(lines[next++], "volumes"));
        var volumes = Repeated(lines, ref next, "volume", NasHostResources.MaximumVolumes).Select(Volume).ToArray();
        if (next != lines.Length - 1)
            throw Invalid();
        try
        {
            var resources = new NasHostResources(Model(Field(lines[6], "model")), listening, ports, volumeStatus, volumes);
            return new(kernel, architecture, DateTimeOffset.FromUnixTimeSeconds(seconds),
                Version(Field(lines[4], "dsm")), Version(Field(lines[5], "container-manager")),
                Version(Field(lines[7], "docker")), Version(Field(lines[8], "compose")),
                inventory, containers, resources);
        }
        catch (ArgumentException)
        {
            throw Invalid();
        }
    }

    /// <summary>Consumes consecutive name=value records; one more than the limit rejects the whole response.</summary>
    private static string[] Repeated(string[] lines, ref int next, string name, int limit)
    {
        var prefix = name + "=";
        var values = new List<string>();
        while (next < lines.Length - 1 && lines[next].StartsWith(prefix, StringComparison.Ordinal))
        {
            values.Add(lines[next][prefix.Length..]);
            next++;
            if (values.Count > limit)
                throw Invalid();
        }
        return values.ToArray();
    }

    private static PreflightFactStatus Status(string value) => value switch
    {
        "available" => PreflightFactStatus.Available,
        "unavailable" => PreflightFactStatus.Unavailable,
        _ => throw Invalid(),
    };

    private static int Port(string value)
    {
        if (value.Length is 0 or > 5 || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            throw Invalid();
        return port;
    }

    private static PreflightVolume Volume(string value)
    {
        var parts = value.Split(':');
        if (parts.Length != 3)
            throw Invalid();
        var total = Kibibytes(parts[1]);
        var available = Kibibytes(parts[2]);
        return new(parts[0], total, available);
    }

    private static long Kibibytes(string value)
    {
        if (value.Length is 0 or > 15 || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var kibibytes))
            throw Invalid();
        return kibibytes * 1024;
    }

    private static PreflightModel Model(string value) => value switch
    {
        "missing" => new(PreflightFactStatus.Missing),
        "unavailable" => new(PreflightFactStatus.Unavailable),
        _ when value.StartsWith("available:", StringComparison.Ordinal) => new(PreflightFactStatus.Available, value[10..]),
        _ => throw Invalid(),
    };

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
