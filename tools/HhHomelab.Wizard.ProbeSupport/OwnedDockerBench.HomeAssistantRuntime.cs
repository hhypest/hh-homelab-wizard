using HhHomelab.Wizard.Infrastructure;

namespace HhHomelab.Wizard.ProbeSupport;

public enum DockerFixtureScenario
{
    Running,
    Exited,
    Oom,
    Restarting,
    Malformed,
    HttpError,
}

public sealed partial class OwnedDockerBench
{
    private string DockerBackend => session + "-docker-api";
    private string DockerProxy => session + "-docker-proxy";

    /// <summary>Real HA/Socket Proxy, synthetic Docker backend. No host socket or bind mounts.</summary>
    public async Task StartHomeAssistantProfileAsync(
        HomeAssistantPayload payload,
        CancellationToken token
    )
    {
        const string image = "ghcr.io/home-assistant/home-assistant:2026.10.0";
        const string proxyImage = "ghcr.io/tecnativa/docker-socket-proxy:v0.5.0";
        if (resources.Count != 0)
            throw new InvalidOperationException("bench.profile.unsupported");
        foreach (var pinned in new[] { image, proxyImage })
            _ = await RunAsync(["image", "inspect", pinned, "--format", "{{.Id}}"], token);
        await CreateNetworkAsync(token);
        var socketVolume = session + "-socket";
        await CreateVolumeAsync(socketVolume, token);
        var backendSeed = session + "-backend-seed";
        resources.Add(("container", backendSeed));
        _ = await RunAsync(
            [
                "create",
                "--interactive",
                "--name",
                backendSeed,
                "--label",
                Label + "=" + session,
                "--network",
                Network,
                "--mount",
                $"type=volume,source={socketVolume},target=/fixture",
                "--entrypoint",
                "python3",
                image,
                "-c",
                "import sys; from pathlib import Path; p=Path('/fixture/docker-api-fixture.py'); assert not p.exists(); p.write_bytes(sys.stdin.buffer.read())",
            ],
            token
        );
        await VerifyOwnershipAsync("container", backendSeed, token);
        using (
            var script = typeof(OwnedDockerBench).Assembly.GetManifestResourceStream(
                "docker-api-fixture.py"
            )!
        )
        {
            var seeded = await RunProcessAsync(
                ["--context", context, "start", "--attach", "--interactive", backendSeed],
                token,
                script
            );
            if (seeded.ExitCode != 0)
                throw new InvalidOperationException("bench.docker-api.seed.failed");
        }
        resources.Add(("container", DockerBackend));
        _ = await RunAsync(
            [
                "create",
                "--name",
                DockerBackend,
                "--label",
                Label + "=" + session,
                "--network",
                Network,
                "--mount",
                $"type=volume,source={socketVolume},target=/fixture",
                "--entrypoint",
                "python3",
                image,
                "/fixture/docker-api-fixture.py",
            ],
            token
        );
        _ = await RunAsync(["start", DockerBackend], token);
        resources.Add(("container", DockerProxy));
        _ = await RunAsync(
            [
                "create",
                "--name",
                DockerProxy,
                "--label",
                Label + "=" + session,
                "--network",
                Network,
                "--network-alias",
                "dockerproxy",
                "--mount",
                $"type=volume,source={socketVolume},target=/fixture,readonly",
                "--env",
                "SOCKET_PATH=/fixture/docker.sock",
                "--env",
                "CONTAINERS=1",
                "--env",
                "INFO=1",
                "--env",
                "IMAGES=1",
                "--env",
                "VERSION=1",
                "--env",
                "POST=0",
                proxyImage,
            ],
            token
        );
        _ = await RunAsync(["start", DockerProxy], token);
        await SeedHomeAssistantPayloadAsync(payload, image, token);
        resources.Add(("container", Container));
        _ = await RunAsync(
            [
                "create",
                "--name",
                Container,
                "--label",
                Label + "=" + session,
                "--network",
                Network,
                "--publish",
                "127.0.0.1::8123",
                "--mount",
                $"type=volume,source={Volume},target=/config",
                "--env",
                "DOCKER_PROXY_URL=http://dockerproxy:2375",
                "--log-driver",
                "json-file",
                "--log-opt",
                "max-size=1m",
                "--log-opt",
                "max-file=1",
                image,
            ],
            token
        );
        _ = await RunAsync(["start", Container], token);
        Endpoint = await ReadBindingAsync(Container, 8123, token);
        await WaitHomeAssistantAsync(token);
    }

    private async Task SeedHomeAssistantPayloadAsync(
        HomeAssistantPayload payload,
        string image,
        CancellationToken token
    )
    {
        await CreateVolumeAsync(Volume, token);
        var seed = session + "-seed";
        resources.Add(("container", seed));
        _ = await RunAsync(
            [
                "create",
                "--interactive",
                "--name",
                seed,
                "--label",
                Label + "=" + session,
                "--network",
                Network,
                "--mount",
                $"type=volume,source={Volume},target=/config",
                "--entrypoint",
                "/bin/sh",
                image,
                "-c",
                "test ! -e /config/configuration.yaml && exec tar -xpf - -C /config",
            ],
            token
        );
        await VerifyOwnershipAsync("container", seed, token);
        using var archive = new MemoryStream();
        await payload.WriteTarAsync(archive, token);
        archive.Position = 0;
        var result = await RunProcessAsync(
            ["--context", context, "start", "--attach", "--interactive", seed],
            token,
            archive
        );
        if (result.ExitCode != 0 || (await RunAsync(["wait", seed], token)).Trim() != "0")
            throw new InvalidOperationException("bench.ha.profile.seed.failed");
    }

    public async Task SetDockerFixtureScenarioAsync(
        DockerFixtureScenario scenario,
        CancellationToken token
    )
    {
        var value = scenario switch
        {
            DockerFixtureScenario.Running => "running",
            DockerFixtureScenario.Exited => "exited",
            DockerFixtureScenario.Oom => "oom",
            DockerFixtureScenario.Restarting => "restarting",
            DockerFixtureScenario.Malformed => "malformed",
            DockerFixtureScenario.HttpError => "http-error",
            _ => throw new ArgumentException("bench.scenario.invalid"),
        };
        await VerifyOwnershipAsync("container", DockerBackend, token);
        _ = await RunAsync(
            [
                "exec",
                DockerBackend,
                "python3",
                "-c",
                "import os,sys; from pathlib import Path; p=Path('/fixture/scenario.tmp'); p.write_text(sys.argv[1]); os.replace(p,'/fixture/scenario')",
                value,
            ],
            token
        );
    }

    public async Task SetDockerProxyRunningAsync(bool running, CancellationToken token)
    {
        await VerifyOwnershipAsync("container", DockerProxy, token);
        _ = await RunAsync(
            running ? ["start", DockerProxy] : ["stop", "--time", "5", DockerProxy],
            token
        );
    }

    public async Task VerifyDockerProxyPolicyAsync(CancellationToken token)
    {
        await VerifyOwnershipAsync("container", Container, token);
        // Request originates from HA, not the developer host. A forbidden POST must never reach the backend.
        const string script = """
            import time, urllib.request, urllib.error
            u = 'http://dockerproxy:2375'
            deadline = time.monotonic() + 30
            while True:
                try:
                    with urllib.request.urlopen(u + '/version', timeout=2) as response:
                        assert response.status == 200
                    break
                except (urllib.error.URLError, TimeoutError, ConnectionError):
                    if time.monotonic() >= deadline:
                        raise
                    time.sleep(0.25)
            request = urllib.request.Request(u + '/containers/radarr/stop', data=b'', method='POST')
            try:
                urllib.request.urlopen(request, timeout=5)
                raise AssertionError()
            except urllib.error.HTTPError as error:
                assert error.code == 403
            print('policy-passed')
            """;
        var result = await RunAsync(["exec", Container, "python3", "-c", script], token);
        if (result.Trim() != "policy-passed")
            throw new InvalidOperationException("bench.proxy.policy.failed");
    }

    public async Task CheckHomeAssistantActionsAsync(CancellationToken token)
    {
        await VerifyOwnershipAsync("container", Container, token);
        using var script = typeof(OwnedDockerBench).Assembly.GetManifestResourceStream(
            "ha-actions-fixture.py"
        )!;
        var result = await RunProcessAsync(
            ["--context", context, "exec", "--interactive", Container, "python3", "-"],
            token,
            script,
            TimeSpan.FromMinutes(2)
        );
        using var json = System.Text.Json.JsonDocument.Parse(result.Output);
        var root = json.RootElement;
        Console.WriteLine(
            "ha-actions.passed_cases: " + root.GetProperty("passed_cases").GetInt32()
        );
        if (
            root.TryGetProperty("failed_case", out var failed)
            && failed.GetString()
                is "confirmed"
                    or "resumed"
                    or "pause-ignored"
                    or "pause-error"
                    or "source-lost"
                    or "protect-warning"
                    or "protect-grace"
                    or "ended-warning"
                    or "toast-error"
                    or "manual-on"
                    or "cancel-warning"
                    or "cancel-grace"
                    or "unavailable-warning"
                    or "resume-ok"
                    or "wrong-item"
                    or "missing-session"
                    or "stale-resume"
                    or "app-failed"
        )
            Console.WriteLine("ha-actions.failed_case: " + failed.GetString());
        if (
            root.TryGetProperty("error_kind", out var kind)
            && kind.GetString()
                is "TypeError"
                    or "AttributeError"
                    or "KeyError"
                    or "AssertionError"
                    or "HomeAssistantError"
                    or "other"
        )
            Console.WriteLine("ha-actions.error_kind: " + kind.GetString());
        if (
            result.ExitCode != 0
            || root.GetProperty("status").GetString() != "passed"
            || root.GetProperty("passed_cases").GetInt32() != 18
        )
            throw new InvalidOperationException("bench.ha.actions.failed");
        Console.WriteLine(
            "ha-actions: actual Script engine, accelerated copies, fake device services passed"
        );
    }
}
