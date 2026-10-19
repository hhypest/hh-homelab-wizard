using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.ProbeSupport;

public sealed partial class OwnedDockerBench
{
    private const string InnerImage = "wizard-preflight-fixture:1";

    /// <summary>The explicit Docker preflight mode needs a privileged disposable container.
    /// It is restricted by policy to Linux GitHub-hosted jobs; never a local/NAS setup action.</summary>
    public static void RequireHostedDockerFixture()
    {
        if (!OperatingSystem.IsLinux())
            throw new InvalidOperationException("bench.ssh.docker.github-hosted-required");
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
            throw new InvalidOperationException("bench.ssh.docker.github-hosted-required");
        if (Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("bench.ssh.docker.github-hosted-required");
        if (Environment.GetEnvironmentVariable("RUNNER_OS") != "Linux")
            throw new InvalidOperationException("bench.ssh.docker.github-hosted-required");
    }

    public async Task StartSshDockerFixtureAsync(CancellationToken token)
    {
        RequireHostedDockerFixture();
        await StartSshFixtureAsync(withDocker: true, token);
        await WaitForInnerDockerAsync(token);
        var outerId = (await RunAsync(["info", "--format", "{{.ID}}"], token)).Trim();
        var innerId = (await InnerDockerAsync(["info", "--format", "{{.ID}}"], token)).Trim();
        if (string.IsNullOrEmpty(innerId) || innerId == outerId)
            throw new InvalidOperationException("bench.ssh.docker.daemon-isolation.failed");
        var docker = (await InnerDockerAsync(["version", "--format", "{{.Server.Version}}"], token)).Trim();
        var compose = (await InnerDockerAsync(["compose", "version", "--short"], token)).Trim().TrimStart('v');
        if (docker != "29.8.2" || compose != "5.1.4")
            throw new InvalidOperationException("bench.ssh.docker.version.failed");
        Console.WriteLine("ssh-docker.daemon-isolation: passed");
        Console.WriteLine("ssh-docker.versions: engine=" + docker + "; compose=" + compose);
    }

    private async Task RequireInnerDockerOwnershipAsync(CancellationToken token)
    {
        RequireHostedDockerFixture();
        if (!sshDockerMode)
            throw new InvalidOperationException("bench.mode.conflict");
        await RequireSshOwnershipAsync(token);
        using var inspected = JsonDocument.Parse(await RunAsync(["container", "inspect", Container], token));
        var config = inspected.RootElement[0].GetProperty("HostConfig");
        if (!config.GetProperty("Privileged").GetBoolean())
            throw new InvalidOperationException("bench.ssh.docker.isolation.failed");
        if (config.GetProperty("NetworkMode").GetString() != Network)
            throw new InvalidOperationException("bench.ssh.docker.isolation.failed");
        if (config.GetProperty("PidMode").GetString() == "host")
            throw new InvalidOperationException("bench.ssh.docker.isolation.failed");
        if (config.GetProperty("IpcMode").GetString() != "private")
            throw new InvalidOperationException("bench.ssh.docker.isolation.failed");
        if (config.GetProperty("CgroupnsMode").GetString() != "private")
            throw new InvalidOperationException("bench.ssh.docker.isolation.failed");
    }

    private async Task<string> InnerDockerAsync(string[] arguments, CancellationToken token)
    {
        await RequireInnerDockerOwnershipAsync(token);
        return await RunAsync(new[] { "exec", Container, "docker", "--host", "unix:///var/run/docker.sock" }.Concat(arguments), token);
    }

    private async Task WaitForInnerDockerAsync(CancellationToken token)
    {
        await RequireInnerDockerOwnershipAsync(token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(40));
        while (true)
        {
            var result = await RunProcessAsync(["--context", context, "exec", Container, "docker", "--host",
                "unix:///var/run/docker.sock", "version", "--format", "{{.Server.Version}}"], deadline.Token);
            if (result.ExitCode == 0)
                return;
            await Task.Delay(200, deadline.Token);
        }
    }

    /// <summary>Fixture setup only: import a tiny owned rootfs, without pull/build or running code.</summary>
    public async Task ImportSshDockerFixtureImageAsync(CancellationToken token)
    {
        await RequireInnerDockerOwnershipAsync(token);
        using var archive = new MemoryStream();
        using (var writer = new TarWriter(archive, TarEntryFormat.Ustar, leaveOpen: true))
        {
            using var marker = new MemoryStream("owned-preflight-fixture\n"u8.ToArray());
            writer.WriteEntry(new UstarTarEntry(TarEntryType.RegularFile, "fixture-marker") { DataStream = marker });
        }
        archive.Position = 0;
        var result = await RunProcessAsync(["--context", context, "exec", "--interactive", Container, "docker",
            "--host", "unix:///var/run/docker.sock", "image", "import", "--change", "CMD [\"/fixture-not-started\"]",
            "-", InnerImage], token, archive);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("bench.ssh.docker.import.failed");
    }

    public async Task<string> CreateSshDockerFixtureResourceAsync(int index, CancellationToken token)
    {
        if (index is < 1 or > 2)
            throw new InvalidOperationException("bench.ssh.docker.resource.invalid");
        var id = (await InnerDockerAsync(["container", "create", "--network", "none", "--label", Label + "=" + session,
            "--name", "wizard-preflight-" + index, InnerImage], token)).Trim();
        if (!ResourceIdentity.IsFingerprint(id))
            throw new InvalidOperationException("bench.ssh.docker.resource.invalid");
        return id;
    }

    /// <summary>Hash logical resource IDs and states; never return container configuration/logs.</summary>
    public async Task<string> ObserveSshDockerResourcesAsync(CancellationToken token)
    {
        var containers = await InnerDockerAsync(["container", "ls", "--all", "--no-trunc", "--format", "{{.ID}}|{{.State}}|{{.Image}}"], token);
        var images = await InnerDockerAsync(["image", "ls", "--quiet", "--no-trunc"], token);
        var volumes = await InnerDockerAsync(["volume", "ls", "--quiet"], token);
        var networks = await InnerDockerAsync(["network", "ls", "--quiet", "--no-trunc"], token);
        var parts = new[] { containers, images, volumes, networks }.Select(output =>
            string.Join('\n', output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal)));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n---\n", parts))));
    }

    public async Task SetSshDockerSocketAccessAsync(bool allowed, CancellationToken token)
    {
        await RequireInnerDockerOwnershipAsync(token);
        _ = await RunAsync(["exec", Container, "chmod", allowed ? "660" : "600", "/var/run/docker.sock"], token);
    }

    public async Task SetSshComposeAccessAsync(bool allowed, CancellationToken token)
    {
        await RequireInnerDockerOwnershipAsync(token);
        _ = await RunAsync(["exec", Container, "chmod", allowed ? "755" : "600", "/usr/libexec/docker/cli-plugins/docker-compose"], token);
    }

    public async Task StopSshDockerDaemonAsync(CancellationToken token)
    {
        await RequireInnerDockerOwnershipAsync(token);
        _ = await RunAsync(["exec", Container, "/fixture/docker-control.sh", "stop"], token);
    }

    public async Task StartSshDockerDaemonAsync(CancellationToken token)
    {
        await RequireInnerDockerOwnershipAsync(token);
        _ = await RunAsync(["exec", Container, "/fixture/docker-control.sh", "start"], token);
        await WaitForInnerDockerAsync(token);
    }
}
