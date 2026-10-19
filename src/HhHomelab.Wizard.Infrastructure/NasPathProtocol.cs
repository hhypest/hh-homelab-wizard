using System.Globalization;
using System.Text;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Fixed read-only path inspection. The layout travels as a stdin request; the response
/// carries only typed facts per requested index and never echoes paths or directory names.</summary>
internal static class NasPathProtocol
{
    internal const string RequestMarker = "hh-homelab-wizard:nas-paths-request:v1";

    internal const string ResponseMarker = "hh-homelab-wizard:nas-paths:v1";

    private static readonly Lazy<string> EmbeddedCommand = new(ReadCommand);

    internal static string Command => EmbeddedCommand.Value;

    private static string ReadCommand()
    {
        using var stream = typeof(NasPathProtocol).Assembly.GetManifestResourceStream("nas-paths.sh")
            ?? throw new InvalidDataException("paths.command.missing");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true));
        return reader.ReadToEnd().TrimEnd('\n');
    }

    internal static string Request(MediaPathLayout layout)
    {
        var builder = new StringBuilder();
        builder.Append(RequestMarker).Append('\n');
        foreach (var entry in layout.Entries)
            builder.Append("path=").Append(entry.HostPath).Append('\n');
        builder.Append("end\n");
        return builder.ToString();
    }

    internal static IReadOnlyList<RemotePathObservation> Parse(MediaPathLayout layout, int? exitCode, string output)
    {
        if (exitCode != 0 || output.Length > SshProbeOutput.MaximumBytes)
            throw Invalid();
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        if (lines.Length != layout.Entries.Count + 2)
            throw Invalid();
        if (lines[0] != ResponseMarker || lines[^1] != "end")
            throw Invalid();
        var observations = new List<RemotePathObservation>();
        for (var index = 0; index < layout.Entries.Count; index++)
        {
            var observation = Entry(layout.Entries[index].Role, lines[index + 1]);
            if (!observation.IsValid)
                throw Invalid();
            observations.Add(observation);
        }
        return observations.AsReadOnly();
    }

    private static RemotePathObservation Entry(RemotePathRole role, string line)
    {
        const string prefix = "entry=";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            throw Invalid();
        var fields = line[prefix.Length..].Split(':');
        if (fields.Length != 8)
            throw Invalid();
        var kind = fields[0] switch
        {
            "missing" => RemotePathKind.Missing,
            "directory" => RemotePathKind.Directory,
            "not-directory" => RemotePathKind.NotDirectory,
            "unavailable" => RemotePathKind.Unavailable,
            _ => throw Invalid(),
        };
        var canonical = fields[2] switch
        {
            "same" => RemotePathCanonical.Same,
            "redirected" => RemotePathCanonical.Redirected,
            "unavailable" => RemotePathCanonical.Unavailable,
            _ => throw Invalid(),
        };
        var contents = fields[7] switch
        {
            "not-applicable" => RemotePathContents.NotApplicable,
            "empty" => RemotePathContents.Empty,
            "not-empty" => RemotePathContents.NotEmpty,
            "unavailable" => RemotePathContents.Unavailable,
            _ => throw Invalid(),
        };
        return new(role, kind, (int)Number(fields[1], 2), canonical, Optional(fields[3], 20),
            OptionalInt(fields[4]), OptionalInt(fields[5]), OptionalMode(fields[6]), contents);
    }

    private static long Number(string value, int maximumDigits)
    {
        if (value.Length == 0 || value.Length > maximumDigits)
            throw Invalid();
        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw Invalid();
        return number;
    }

    private static long? Optional(string value, int maximumDigits) => value == "-" ? null : Number(value, maximumDigits);

    private static int? OptionalInt(string value) => value == "-" ? null : (int)Number(value, 9);

    private static int? OptionalMode(string value)
    {
        if (value == "-")
            return null;
        if (value.Length is 0 or > 4 || value.Any(character => character is < '0' or > '7'))
            throw Invalid();
        return Convert.ToInt32(value, 8);
    }

    private static InvalidDataException Invalid() => new("paths.response.invalid");
}
