using System.Globalization;
using System.Text;
using System.Text.Json;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Fixed read-only path inspection. The layout travels as a stdin request; the response
/// carries only typed facts per requested index plus the ownership marker of the configuration
/// root as hex, and never echoes paths or directory names.</summary>
internal static class NasPathProtocol
{
    internal const string RequestMarker = "hh-homelab-wizard:nas-paths-request:v3";

    internal const string ResponseMarker = "hh-homelab-wizard:nas-paths:v3";

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
        builder.Append("owner=").Append(layout.ConfigurationRoot).Append('\n');
        builder.Append("end\n");
        return builder.ToString();
    }

    internal static RemotePathFacts Parse(MediaPathLayout layout, int? exitCode, string output)
    {
        if (exitCode != 0 || output.Length > SshProbeOutput.MaximumBytes)
            throw Invalid();
        var lines = output.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');
        var entriesEnd = layout.Entries.Count + 3;
        if (lines.Length < entriesEnd + 2)
            throw Invalid();
        if (lines[0] != ResponseMarker || lines[^1] != "end")
            throw Invalid();
        const string rootPrefix = "root-device=";
        if (!lines[1].StartsWith(rootPrefix, StringComparison.Ordinal))
            throw Invalid();
        var rootDevice = Number(lines[1][rootPrefix.Length..], 20);
        var identity = Identity(lines[2]);
        var observations = new List<RemotePathObservation>();
        for (var index = 0; index < layout.Entries.Count; index++)
        {
            var observation = Entry(layout.Entries[index].Role, lines[index + 3]);
            if (!observation.IsValid)
                throw Invalid();
            observations.Add(observation);
        }
        var ownership = Ownership(lines[entriesEnd..^1]);
        return new(rootDevice, observations.AsReadOnly(), ownership, identity);
    }

    private static RemoteUnixIdentity Identity(string line)
    {
        const string prefix = "identity=";
        if (!line.StartsWith(prefix, StringComparison.Ordinal))
            throw Invalid();

        var fields = line[prefix.Length..].Split(':');
        if (fields.Length != 2)
            throw Invalid();

        return new(IdentityNumber(fields[0]), IdentityNumber(fields[1]));
    }

    private static int IdentityNumber(string value)
    {
        if (value.Length is 0 or > 10)
            throw Invalid();
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            throw Invalid();

        return number;
    }

    /// <summary>A marker that is present but not a valid schema-1 object is an observation
    /// (Invalid), not a protocol error; malformed framing or hex rejects the whole response.</summary>
    private static OwnershipObservation Ownership(string[] lines)
    {
        const string prefix = "marker=";
        if (lines.Length == 0 || !lines[0].StartsWith(prefix, StringComparison.Ordinal))
            throw Invalid();
        var state = lines[0][prefix.Length..];
        if (state == "staged" && lines.Length == 2)
            return Staged(lines[1]);
        if (state != "present")
        {
            if (lines.Length != 1)
                throw Invalid();
            return state switch
            {
                "absent" => new(OwnershipMarkerState.Absent),
                "staged" => new(OwnershipMarkerState.Staged),
                "invalid" => new(OwnershipMarkerState.Invalid),
                "redirected" => new(OwnershipMarkerState.Redirected),
                "unavailable" => new(OwnershipMarkerState.Unavailable),
                _ => throw Invalid(),
            };
        }
        if (lines.Length != 2)
            throw Invalid();
        var marker = Content(lines[1]);
        return marker is null ? new(OwnershipMarkerState.Invalid) : new(OwnershipMarkerState.Present, marker);
    }

    /// <summary>The common content of the staged temporary files must itself be a valid marker;
    /// a partially written one is Invalid and blocks like any unreadable marker.</summary>
    private static OwnershipObservation Staged(string line)
    {
        var marker = Content(line);
        return marker is null ? new(OwnershipMarkerState.Invalid) : new(OwnershipMarkerState.Staged, marker);
    }

    private static OwnershipMarker? Content(string line)
    {
        const string contentPrefix = "marker-content=";
        if (!line.StartsWith(contentPrefix, StringComparison.Ordinal))
            throw Invalid();
        return OwnershipMarkerJson.Parse(Hex(line[contentPrefix.Length..]));
    }

    private static byte[] Hex(string value)
    {
        if (value.Length > OwnershipMarker.MaximumBytes * 2 || value.Length % 2 != 0)
            throw Invalid();
        if (!value.All(char.IsAsciiHexDigitLower))
            throw Invalid();
        return Convert.FromHexString(value);
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

/// <summary>Strict marker format: one UTF-8 JSON object with exactly schema, installationId and
/// profileFingerprint. Duplicates, unknown properties, trailing data or wrong types are not a marker.</summary>
internal static class OwnershipMarkerJson
{
    private static readonly string[] Properties = ["schema", "installationId", "profileFingerprint"];

    internal static OwnershipMarker? Parse(byte[] bytes)
    {
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
            return Marker(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private static OwnershipMarker? Marker(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        var names = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != Properties.Length || !names.Order().SequenceEqual(Properties.Order()))
            return null;
        if (!root.GetProperty("schema").TryGetInt32(out var schema))
            return null;
        var installation = root.GetProperty("installationId").GetString();
        if (!Guid.TryParseExact(installation, "D", out var installationId))
            return null;
        var profile = root.GetProperty("profileFingerprint").GetString();
        var marker = new OwnershipMarker(schema, installationId, profile ?? "");
        return marker.IsValid ? marker : null;
    }

    /// <summary>Single-line canonical form; the write script compares bytes for idempotency.</summary>
    internal static string Serialize(OwnershipMarker marker)
    {
        if (!marker.IsValid)
            throw new ArgumentException("ownership.marker.invalid");
        return "{\"schema\":" + marker.Schema.ToString(CultureInfo.InvariantCulture)
            + ",\"installationId\":\"" + marker.InstallationId.ToString("D")
            + "\",\"profileFingerprint\":\"" + marker.ProfileFingerprint + "\"}";
    }

}
