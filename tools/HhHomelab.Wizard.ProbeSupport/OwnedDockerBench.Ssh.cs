using System.Formats.Tar;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.ProbeSupport;

public enum SshFixtureFault
{
    Normal,
    Delay,
    Oversized,
    Malformed,
    SudoDenied,
    SftpDenied,
}

public sealed partial class OwnedDockerBench
{
    private static readonly string[] SshFixtureFiles = ["Dockerfile", "sshd_config", "entrypoint.sh", "dispatch.sh", "preflight.sh"];

    private const string SshDefinitionLabel = "com.hh-homelab-wizard.ssh-fixture";

    public SshConnectionTarget SshTarget { get; private set; } = null!;

    private bool sshMode;

    private static byte[] ReadSshFixture(string name)
    {
        using var stream = typeof(OwnedDockerBench).Assembly.GetManifestResourceStream("ssh-fixture." + name)
            ?? throw new InvalidOperationException("bench.ssh.definition.missing");
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static string SshDefinitionHash()
    {
        var entries = SshFixtureFiles.Select(name => Convert.ToHexStringLower(SHA256.HashData(ReadSshFixture(name))) + "  " + name + "\n");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(entries))));
    }

    /// <summary>Build only the embedded public fixture on a local daemon. The digest-pinned base
    /// must already be cached; secrets are generated later and never enter build layers or args.</summary>
    public async Task StartSshFixtureAsync(CancellationToken token)
    {
        if (resources.Count != 0)
            throw new InvalidOperationException("bench.mode.conflict");
        var immutableImage = await BuildSshFixtureAsync(token);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await CreateNetworkAsync(token);
        resources.Add(("container", Container));
        _ = await RunAsync(["create", "--name", Container, "--label", Label + "=" + session,
            "--network", Network, "--publish", $"127.0.0.1:{port}:2222", "--pids-limit", "64", "--memory", "128m", "--cpus", "1",
            "--tmpfs", "/run/sshd:rw,mode=0700", "--tmpfs", "/fixture-state:rw,uid=1000,gid=1000,mode=0700",
            "--log-driver", "json-file", "--log-opt", "max-size=1m", "--log-opt", "max-file=1", immutableImage], token);
        sshMode = true;
        _ = await RunAsync(["start", Container], token);
        var binding = await ReadBindingAsync(Container, 2222, token);
        SshTarget = new("127.0.0.1", binding.Port, "wizard");
        await WaitForSshAsync(token);
        await ReportSshPackagesAsync(token);
    }

    private async Task<string> BuildSshFixtureAsync(CancellationToken token)
    {
        var definition = SshDefinitionHash();
        var image = "hh-homelab-wizard/ssh-fixture:" + definition;
        var dockerfile = Encoding.UTF8.GetString(ReadSshFixture("Dockerfile"));
        var baseImage = dockerfile.Split('\n')[0]["FROM ".Length..].Trim();
        _ = await RunAsync(["image", "inspect", baseImage], token);
        using var archive = CreateSshBuildArchive();
        var build = await RunProcessAsync(["--context", context, "build", "--pull=false", "--label", SshDefinitionLabel + "=" + definition,
            "--tag", image, "-"], token, archive, TimeSpan.FromMinutes(4));
        if (build.ExitCode != 0)
        {
            // Only embedded public build files exist at this point; credentials are created later.
            // Runtime/authentication process streams must never use this diagnostic path.
            var publicOutput = build.Output + build.Error;
            var diagnostic = new string(publicOutput.Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t').Take(12000).ToArray());
            Console.Error.WriteLine("ssh-fixture.public-build: " + diagnostic);
            throw new InvalidOperationException("bench.ssh.build.failed");
        }
        using var inspected = JsonDocument.Parse(await RunAsync(["image", "inspect", image], token));
        var root = inspected.RootElement[0];
        if (root.GetProperty("Config").GetProperty("Labels").GetProperty(SshDefinitionLabel).GetString() != definition)
            throw new InvalidOperationException("bench.ssh.image.invalid");
        if (root.GetProperty("Architecture").GetString() != "amd64")
            throw new InvalidOperationException("bench.ssh.architecture.unsupported");
        Console.WriteLine("ssh-fixture.definition: " + definition);
        Console.WriteLine("ssh-fixture.base: " + baseImage);
        return root.GetProperty("Id").GetString()!;
    }

    private static MemoryStream CreateSshBuildArchive()
    {
        var archive = new MemoryStream();
        // Docker's stdin archive detection does not recognize a leading PAX extended header.
        using (var writer = new TarWriter(archive, TarEntryFormat.Ustar, leaveOpen: true))
        {
            foreach (var name in SshFixtureFiles)
            {
                using var contents = new MemoryStream(ReadSshFixture(name));
                writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, name) { DataStream = contents });
            }
        }
        archive.Position = 0;
        return archive;
    }

    private async Task ReportSshPackagesAsync(CancellationToken token)
    {
        var installed = await RunAsync(["exec", Container, "apk", "info", "-v"], token);
        string[] expectedPrefixes = ["openssh-server-10.", "openssh-sftp-server-10.", "sudo-1."];
        var directPackages = installed.Split('\n', StringSplitOptions.TrimEntries).Where(package =>
            expectedPrefixes.Any(prefix => package.StartsWith(prefix, StringComparison.Ordinal))).ToArray();
        if (directPackages.Length != 3)
            throw new InvalidOperationException("bench.ssh.packages.unavailable");
        Console.WriteLine("ssh-fixture.packages: " + string.Join(' ', directPackages));
    }

    public async Task ConfigureSshCredentialsAsync(string password, string publicKey, CancellationToken token)
    {
        await RequireSshOwnershipAsync(token);
        if (password.Length < 16 || !password.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("bench.ssh.password.invalid");
        if (!publicKey.StartsWith("ssh-rsa ", StringComparison.Ordinal) || publicKey.Contains('\n'))
            throw new InvalidOperationException("bench.ssh.public-key.invalid");
        await SshInputAsync(["exec", "--interactive", Container, "chpasswd"], "wizard:" + password + "\n", token);
        await SshInputAsync(["exec", "--interactive", Container, "sh", "-c",
            "umask 077; cat > /home/wizard/.ssh/authorized_keys; chown 1000:1000 /home/wizard/.ssh/authorized_keys"], publicKey + "\n", token);
    }

    private async Task SshInputAsync(string[] arguments, string payload, CancellationToken token)
    {
        var bytes = Encoding.UTF8.GetBytes(payload);
        try
        {
            using var input = new MemoryStream(bytes);
            var result = await RunProcessAsync(new[] { "--context", context }.Concat(arguments), token, input);
            if (result.ExitCode != 0)
                throw new InvalidOperationException("bench.ssh.input.failed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public async Task<SshHostKey> ReadSshHostKeyAsync(CancellationToken token)
    {
        await RequireSshOwnershipAsync(token);
        var fields = (await RunAsync(["exec", Container, "cat", "/run/sshd/hostkey.pub"], token)).Trim().Split(' ');
        if (fields.Length < 2 || fields[0] != "ssh-ed25519")
            throw new InvalidOperationException("bench.ssh.host-key.invalid");
        return SshHostKey.FromPublicKey(SshTarget.EndpointFingerprint, fields[0], Convert.FromBase64String(fields[1]));
    }

    public async Task SetSshFaultAsync(SshFixtureFault fault, CancellationToken token)
    {
        await RequireSshOwnershipAsync(token);
        var mode = fault switch
        {
            SshFixtureFault.Normal => "normal",
            SshFixtureFault.Delay => "delay",
            SshFixtureFault.Oversized => "oversized",
            SshFixtureFault.Malformed => "malformed",
            SshFixtureFault.SudoDenied => "sudo-denied",
            SshFixtureFault.SftpDenied => "sftp-denied",
            _ => throw new InvalidOperationException("bench.ssh.fault.invalid"),
        };
        await SshInputAsync(["exec", "--interactive", Container, "sh", "-c",
            "rm -f /fixture-state/identity-started; cat > /fixture-state/mode"], mode, token);
    }

    public async Task WaitForSshCommandAsync(CancellationToken token)
    {
        await RequireSshOwnershipAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        while (true)
        {
            var result = await RunProcessAsync(["--context", context, "exec", Container, "test", "-f", "/fixture-state/identity-started"], deadline.Token);
            if (result.ExitCode == 0)
                return;
            await Task.Delay(100, deadline.Token);
        }
    }

    public async Task RotateSshHostKeyAsync(CancellationToken token)
    {
        await RequireSshOwnershipAsync(token);
        _ = await RunAsync(["exec", Container, "sh", "-c",
            "ssh-keygen -q -t ed25519 -N '' -f /run/sshd/next; mv /run/sshd/next /run/sshd/hostkey; mv /run/sshd/next.pub /run/sshd/hostkey.pub; kill -HUP 1"], token);
        await WaitForSshAsync(token);
    }

    public async Task KillSshFixtureAsync(CancellationToken token)
    {
        await RequireSshOwnershipAsync(token);
        _ = await RunAsync(["kill", "--signal", "KILL", Container], token);
    }

    public async Task RestartSshFixtureAsync(CancellationToken token)
    {
        await RequireSshOwnershipAsync(token);
        _ = await RunAsync(["start", Container], token);
        var binding = await ReadBindingAsync(Container, 2222, token);
        if (binding.Port != SshTarget.Port)
            throw new InvalidOperationException("bench.ssh.endpoint.changed");
        Console.WriteLine("ssh-runtime.fixture-restart: started");
        await WaitForSshAsync(token);
    }

    private async Task RequireSshOwnershipAsync(CancellationToken token)
    {
        if (!sshMode)
            throw new InvalidOperationException("bench.mode.conflict");
        await VerifyOwnershipAsync("container", Container, token);
    }

    private async Task WaitForSshAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            using var client = new TcpClient();
            try
            {
                await client.ConnectAsync(SshTarget.Host, SshTarget.Port, deadline.Token);
                using var reader = new StreamReader(client.GetStream());
                var banner = await reader.ReadLineAsync(deadline.Token);
                if (banner is not null && banner.StartsWith("SSH-2.0-OpenSSH_", StringComparison.Ordinal))
                    return;
            }
            catch (SocketException) { }
            catch (IOException) { }
            await Task.Delay(100, deadline.Token);
        }
    }
}
