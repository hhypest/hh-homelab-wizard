using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.NasReadOnlyCheck;

public enum CredentialKind
{
    Password,
    PrivateKey,
}

/// <summary>Command line of the read-only check. Secrets are never accepted as arguments: the password and a
/// key passphrase are typed at a prompt, the private key stays in a local file.</summary>
public sealed record CheckOptions(
    SshConnectionTarget Target,
    CredentialKind Credential,
    string? KeyPath,
    string? HostKeyAlgorithm,
    string? HostKeyFingerprint,
    MediaPathLayout? Layout,
    string? NetworkName,
    string? ReportPath)
{
    public const string Usage = """
        Usage: NasReadOnlyCheck --host <address> --user <name> (--password | --key <private key file>)
                                [--port 22] [--host-key "<algorithm> SHA256:<fingerprint>"]
                                [--data-root /volumeN/... --config-root /volumeN/...] [--network <name>]
                                [--report <file.json>]

        Only reads. The first run without --host-key prints the server key and stops; compare it on the NAS
        and run again with --host-key. The password and a key passphrase are asked interactively.
        """;

    /// <summary>Null with an error code when the arguments are incomplete or invalid.</summary>
    public static (CheckOptions? Options, string? Error) Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index++)
        {
            var name = arguments[index];
            var flag = name is "--password";
            var known = name is "--host" or "--port" or "--user" or "--key" or "--host-key" or "--data-root"
                or "--config-root" or "--network" or "--report";
            if (!flag && !known)
                return (null, "arguments.unknown");
            if (values.ContainsKey(name))
                return (null, "arguments.duplicate");
            if (flag)
            {
                values[name] = null;
                continue;
            }

            if (index + 1 >= arguments.Count)
                return (null, "arguments.value-missing");

            index++;
            values[name] = arguments[index];
        }

        return Build(values);
    }

    private static (CheckOptions? Options, string? Error) Build(Dictionary<string, string?> values)
    {
        var port = 22;
        if (values.TryGetValue("--port", out var portText) && !int.TryParse(portText, out port))
            return (null, "arguments.port");

        var target = new SshConnectionTarget(Value(values, "--host") ?? "", port, Value(values, "--user") ?? "");
        if (!target.IsValid)
            return (null, "arguments.target");

        var password = values.ContainsKey("--password");
        var keyPath = Value(values, "--key");
        if (password == (keyPath is not null))
            return (null, "arguments.credential");

        var (algorithm, fingerprint, hostKeyError) = ParseHostKey(target, Value(values, "--host-key"));
        if (hostKeyError is not null)
            return (null, hostKeyError);

        var (layout, layoutError) = ParseLayout(Value(values, "--data-root"), Value(values, "--config-root"));
        if (layoutError is not null)
            return (null, layoutError);

        var network = Value(values, "--network");
        if (network is not null && !DockerNetworkSnapshot.IsValidName(network))
            return (null, "arguments.network");
        if (network is not null && layout is null)
            return (null, "arguments.network-needs-layout");

        var kind = password ? CredentialKind.Password : CredentialKind.PrivateKey;
        var options = new CheckOptions(target, kind, keyPath, algorithm, fingerprint, layout, network, Value(values, "--report"));
        return (options, null);
    }

    /// <summary>The key exactly as printed by the first run: algorithm, a space and the SHA256 fingerprint.</summary>
    private static (string? Algorithm, string? Fingerprint, string? Error) ParseHostKey(SshConnectionTarget target, string? text)
    {
        if (text is null)
            return (null, null, null);

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return (null, null, "arguments.host-key");

        var key = new SshHostKey(target.EndpointFingerprint, parts[0], parts[1]);
        if (!key.IsValid)
            return (null, null, "arguments.host-key");

        return (parts[0], parts[1], null);
    }

    private static (MediaPathLayout? Layout, string? Error) ParseLayout(string? dataRoot, string? configurationRoot)
    {
        if (dataRoot is null && configurationRoot is null)
            return (null, null);
        if (dataRoot is null || configurationRoot is null)
            return (null, "arguments.layout-incomplete");

        try
        {
            return (new MediaPathLayout(dataRoot, configurationRoot), null);
        }
        catch (ArgumentException)
        {
            return (null, "arguments.layout");
        }
    }

    private static string? Value(Dictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out var value) ? value : null;

    public SshHostKey? ApprovedKey => HostKeyAlgorithm is null || HostKeyFingerprint is null
        ? null
        : new SshHostKey(Target.EndpointFingerprint, HostKeyAlgorithm, HostKeyFingerprint);
}
