using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

if (args.Length != 0)
{
    Console.Error.WriteLine("Usage: SshDockerProbe; local Linux Docker and cached fixture base required.");
    return 2;
}
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
Console.CancelKeyPress += (_, signal) =>
{
    signal.Cancel = true;
    deadline.Cancel();
};
OwnedDockerBench? bench = null;
var exitCode = 1;
try
{
    bench = await OwnedDockerBench.ConnectAsync(deadline.Token);
    await bench.StartSshFixtureAsync(deadline.Token);
    await SshScenarios.RunAsync(bench, deadline.Token);
    exitCode = 0;
}
catch (Exception error)
{
    var code = error is SshScenarioException scenario ? scenario.Code : error.GetType().Name;
    if (error is InvalidOperationException && error.Message.StartsWith("bench.", StringComparison.Ordinal))
        code = error.Message;
    Console.Error.WriteLine("ssh-runtime.failure: " + code);
}
finally
{
    if (bench is not null)
    {
        await bench.DisposeAsync();
        if (!bench.CleanupSucceeded)
            exitCode = 1;
    }
}
if (exitCode == 0)
    Console.WriteLine("ssh-runtime.all: passed");
return exitCode;

internal static class SshScenarios
{
    internal static async Task RunAsync(OwnedDockerBench bench, CancellationToken token)
    {
        using var key = FixtureKey.Create();
        using var otherKey = FixtureKey.Create();
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await bench.ConfigureSshCredentialsAsync(password, key.PublicKey, token);
        var pin = await bench.ReadSshHostKeyAsync(token);

        using var wrongPassword = SshProbeCredentials.Password("synthetic-wrong-password");
        var first = await new SshConnectionProbe(bench.SshTarget, wrongPassword).ProbeAsync(null, token);
        Expect("first-use", first, SshProbeStatus.HostKeyApprovalRequired);
        Require(first.HostKey == pin, "first-use.evidence");
        Expect("wrong-password", await new SshConnectionProbe(bench.SshTarget, wrongPassword).ProbeAsync(pin, token), SshProbeStatus.AuthenticationRequired);

        using var credentials = SshProbeCredentials.Password(password);
        await ReadyAsync(bench, credentials, pin, token);
        using var keyCredentials = SshProbeCredentials.PrivateKey(key.PrivateKey, key.Passphrase);
        await ReadyAsync(bench, keyCredentials, pin, token);
        using var rejectedKey = SshProbeCredentials.PrivateKey(otherKey.PrivateKey, otherKey.Passphrase);
        Expect("wrong-key", await new SshConnectionProbe(bench.SshTarget, rejectedKey).ProbeAsync(pin, token), SshProbeStatus.AuthenticationRequired);
        Console.WriteLine("ssh-runtime.password-key-sftp: passed");

        await VerifyFaultsAsync(bench, credentials, pin, token);
        await VerifyCancellationAsync(bench, credentials, pin, token);
        await VerifyKeyRotationAsync(bench, credentials, pin, token);
        pin = await bench.ReadSshHostKeyAsync(token);
        await VerifyDisconnectAsync(bench, credentials, pin, token);
    }

    private static async Task ReadyAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin, CancellationToken token)
    {
        var result = await new SshConnectionProbe(bench.SshTarget, credentials).ProbeAsync(pin, token);
        Expect("ready", result, SshProbeStatus.Ready);
        Require(result.Capabilities == new SshHostCapabilities(1000, true), "ready.capabilities");
    }

    private static async Task VerifyFaultsAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin, CancellationToken token)
    {
        foreach (var fault in new[] { SshFixtureFault.Malformed, SshFixtureFault.Oversized })
        {
            await bench.SetSshFaultAsync(fault, token);
            var result = await new SshConnectionProbe(bench.SshTarget, credentials).ProbeAsync(pin, token);
            Expect("invalid-output." + fault, result, SshProbeStatus.InvalidResponse);
        }
        await bench.SetSshFaultAsync(SshFixtureFault.SudoDenied, token);
        var denied = await new SshConnectionProbe(bench.SshTarget, credentials).ProbeAsync(pin, token);
        Expect("sudo-list-denied", denied, SshProbeStatus.Ready);
        Require(denied.Capabilities is { CanListSudoWithoutPrompt: false }, "sudo-list-denied.capabilities");
        await bench.SetSshFaultAsync(SshFixtureFault.SftpDenied, token);
        var sftp = await new SshConnectionProbe(bench.SshTarget, credentials, TimeSpan.FromSeconds(5)).ProbeAsync(pin, token);
        var safeFailure = sftp.Status is SshProbeStatus.SftpUnavailable or SshProbeStatus.TransportFailure;
        Require(safeFailure && sftp.Capabilities is null, "sftp-unavailable." + sftp.Status);
        Console.WriteLine("ssh-runtime.sftp-unavailable: " + sftp.Status);
        await bench.SetSshFaultAsync(SshFixtureFault.Normal, token);
        await ReadyAsync(bench, credentials, pin, token);
        Console.WriteLine("ssh-runtime.response-and-sudo-policy: passed");
    }

    private static async Task VerifyCancellationAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin, CancellationToken token)
    {
        await bench.SetSshFaultAsync(SshFixtureFault.Delay, token);
        using (var caller = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            var probe = new SshConnectionProbe(bench.SshTarget, credentials);
            var pending = probe.ProbeAsync(pin, caller.Token);
            try
            {
                await bench.WaitForSshCommandAsync(token);
                Expect("busy", await probe.ProbeAsync(pin, token), SshProbeStatus.Busy);
                caller.Cancel();
                Expect("caller-cancel", await pending.WaitAsync(TimeSpan.FromSeconds(6), token), SshProbeStatus.Cancelled);
            }
            finally
            {
                caller.Cancel();
                await DrainAsync(pending);
            }
        }
        await bench.SetSshFaultAsync(SshFixtureFault.Delay, token);
        var limited = new SshConnectionProbe(bench.SshTarget, credentials, TimeSpan.FromSeconds(3));
        var timed = limited.ProbeAsync(pin, token);
        await bench.WaitForSshCommandAsync(token);
        Expect("command-timeout", await timed.WaitAsync(TimeSpan.FromSeconds(6), token), SshProbeStatus.TimedOut);
        await bench.SetSshFaultAsync(SshFixtureFault.Normal, token);
        await ReadyAsync(bench, credentials, pin, token);
        Console.WriteLine("ssh-runtime.cancel-timeout-busy: passed");
    }

    private static async Task VerifyKeyRotationAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin, CancellationToken token)
    {
        await bench.RotateSshHostKeyAsync(token);
        var replacement = await bench.ReadSshHostKeyAsync(token);
        Require(replacement != pin, "rotation.key-unchanged");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            var observed = await new SshConnectionProbe(bench.SshTarget, credentials).ProbeAsync(pin, limit.Token);
            if (observed.Status == SshProbeStatus.HostKeyChanged)
            {
                Require(observed.HostKey == replacement, "rotation.evidence");
                break;
            }
            Expect("rotation.wait", observed, SshProbeStatus.Ready);
            await Task.Delay(100, limit.Token);
        }
        await ReadyAsync(bench, credentials, replacement, token);
        Console.WriteLine("ssh-runtime.host-key-rotation: passed");
    }

    private static async Task VerifyDisconnectAsync(OwnedDockerBench bench, SshProbeCredentials credentials, SshHostKey pin, CancellationToken token)
    {
        await bench.SetSshFaultAsync(SshFixtureFault.Delay, token);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        var pending = new SshConnectionProbe(bench.SshTarget, credentials).ProbeAsync(pin, caller.Token);
        try
        {
            await bench.WaitForSshCommandAsync(token);
            await bench.KillSshFixtureAsync(token);
            Expect("connection-loss", await pending.WaitAsync(TimeSpan.FromSeconds(6), token), SshProbeStatus.TransportFailure);
            Console.WriteLine("ssh-runtime.connection-loss: passed");
        }
        finally
        {
            caller.Cancel();
            await DrainAsync(pending);
        }
        await bench.RestartSshFixtureAsync(token);
        var fresh = await bench.ReadSshHostKeyAsync(token);
        Require(fresh != pin, "restart.key-unchanged");
        Expect("restart-old-pin", await new SshConnectionProbe(bench.SshTarget, credentials).ProbeAsync(pin, token), SshProbeStatus.HostKeyChanged);
        await ReadyAsync(bench, credentials, fresh, token);
        Console.WriteLine("ssh-runtime.disconnect-and-reconnect: passed");
    }

    private static async Task DrainAsync(Task pending)
    {
        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(6));
        }
        catch (Exception) { }
    }

    private static void Expect(string stage, SshProbeResult result, SshProbeStatus expected) => Require(result.Status == expected, stage + "." + result.Status);

    private static void Require(bool satisfied, string code)
    {
        if (!satisfied)
            throw new SshScenarioException(code);
    }
}

internal sealed class SshScenarioException(string code) : Exception
{
    internal string Code { get; } = code;
}

internal sealed class FixtureKey(byte[] privateKey, string passphrase, string publicKey) : IDisposable
{
    internal byte[] PrivateKey { get; } = privateKey;

    internal string Passphrase { get; } = passphrase;

    internal string PublicKey { get; } = publicKey;

    internal static FixtureKey Create()
    {
        using var rsa = RSA.Create(2048);
        var passphrase = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var privateKey = Encoding.UTF8.GetBytes(rsa.ExportEncryptedPkcs8PrivateKeyPem(passphrase,
            new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 10000)));
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        using var publicKey = new MemoryStream();
        WriteField(publicKey, "ssh-rsa"u8);
        WriteInteger(publicKey, parameters.Exponent!);
        WriteInteger(publicKey, parameters.Modulus!);
        return new(privateKey, passphrase, "ssh-rsa " + Convert.ToBase64String(publicKey.ToArray()));
    }

    private static void WriteInteger(Stream output, byte[] value)
    {
        WriteField(output, (value[0] & 0x80) == 0 ? value : [0, .. value]);
    }

    private static void WriteField(Stream output, ReadOnlySpan<byte> value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        output.Write(length);
        output.Write(value);
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(PrivateKey);
}
