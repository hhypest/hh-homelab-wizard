using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.NasReadOnlyCheck;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class NasReadOnlyCheckTests
{
    private const string HostKey = "ssh-ed25519 SHA256:fnLZ4arxe8an4Gct0ZQPCGBwm6LgCQlpTr+HnPxymAU";

    [Fact]
    public void FullCommandLineBindsKeyToTheTargetEndpoint()
    {
        var (options, error) = CheckOptions.Parse(["--host", "192.168.3.53", "--user", "wizard", "--key", "C:\\keys\\nas",
            "--host-key", HostKey, "--data-root", "/volume1/data", "--config-root", "/volume1/docker/hhw",
            "--network", "media-test", "--report", "nas.json"]);

        Assert.Null(error);
        Assert.Equal(CredentialKind.PrivateKey, options!.Credential);
        Assert.Equal(22, options.Target.Port);
        Assert.Equal(options.Target.EndpointFingerprint, options.ApprovedKey!.EndpointFingerprint);
        Assert.True(options.ApprovedKey.IsValid);
        Assert.Equal("/volume1/docker/hhw", options.Layout!.ConfigurationRoot);
    }

    [Fact]
    public void FirstRunWithoutHostKeyHasNoApprovedKey()
    {
        var (options, error) = CheckOptions.Parse(["--host", "192.168.3.53", "--user", "wizard", "--password"]);

        Assert.Null(error);
        Assert.Equal(CredentialKind.Password, options!.Credential);
        Assert.Null(options.ApprovedKey);
        Assert.Null(options.Layout);
    }

    [Theory]
    [InlineData("arguments.unknown", "--host", "192.168.3.53", "--user", "wizard", "--password", "--pass", "secret")]
    [InlineData("arguments.credential", "--host", "192.168.3.53", "--user", "wizard")]
    [InlineData("arguments.credential", "--host", "192.168.3.53", "--user", "wizard", "--password", "--key", "nas")]
    [InlineData("arguments.target", "--host", "", "--user", "wizard", "--password")]
    [InlineData("arguments.port", "--host", "192.168.3.53", "--port", "ssh", "--user", "wizard", "--password")]
    [InlineData("arguments.host-key", "--host", "192.168.3.53", "--user", "wizard", "--password", "--host-key", "SHA256:short")]
    [InlineData("arguments.layout-incomplete", "--host", "192.168.3.53", "--user", "wizard", "--password", "--data-root", "/volume1/data")]
    [InlineData("arguments.layout", "--host", "192.168.3.53", "--user", "wizard", "--password", "--data-root", "/tmp/data", "--config-root", "/volume1/c")]
    [InlineData("arguments.network-needs-layout", "--host", "192.168.3.53", "--user", "wizard", "--password", "--network", "media")]
    [InlineData("arguments.network", "--host", "192.168.3.53", "--user", "wizard", "--password", "--data-root", "/volume1/data",
        "--config-root", "/volume1/c", "--network", "media\"x")]
    [InlineData("arguments.duplicate", "--host", "192.168.3.53", "--host", "192.168.3.54", "--user", "wizard", "--password")]
    [InlineData("arguments.value-missing", "--host", "192.168.3.53", "--user")]
    public void InvalidCommandLinesAreRefusedBeforeConnecting(string expected, params string[] arguments)
    {
        var (options, error) = CheckOptions.Parse(arguments);

        Assert.Null(options);
        Assert.Equal(expected, error);
    }

    [Theory]
    [InlineData("+22")]
    [InlineData(" 22")]
    [InlineData("0")]
    [InlineData("65536")]
    public void PortIsPlainDecimalInRange(string port)
    {
        var (options, error) = CheckOptions.Parse(["--host", "192.168.3.53", "--port", port, "--user", "wizard", "--password"]);

        Assert.Null(options);
        Assert.True(error is "arguments.port" or "arguments.target");
    }

    [Fact]
    public async Task UnreachableHostIsNotReportedAsKeyApproval()
    {
        var (options, _) = CheckOptions.Parse(["--host", "127.0.0.1", "--port", "1", "--user", "wizard", "--password"]);
        using var credentials = SshProbeCredentials.Password("not-a-real-password");
        using var output = new StringWriter();
        var check = new ReadOnlyCheck(options!, credentials, output);

        var exitCode = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.DoesNotContain("ssh.host-key", output.ToString(), StringComparison.Ordinal);
        Assert.False(check.Report.ContainsKey("endpoint"));
        Assert.DoesNotContain("not-a-real-password", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void PasswordIsNeverAnArgument()
    {
        var (options, error) = CheckOptions.Parse(["--host", "192.168.3.53", "--user", "wizard", "--password", "secret"]);

        Assert.Null(options);
        Assert.Equal("arguments.unknown", error);
    }
}
