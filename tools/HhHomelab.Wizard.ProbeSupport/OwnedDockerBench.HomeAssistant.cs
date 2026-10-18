using HhHomelab.Wizard.Infrastructure;
using System.Net;

namespace HhHomelab.Wizard.ProbeSupport;

public sealed partial class OwnedDockerBench
{
    /// <summary>Only a fresh owned fixture. No host socket, host paths, devices or .storage edits.</summary>
    public async Task StartHomeAssistantAsync(string image, CancellationToken token)
    {
        if (image != PinnedRuntimeProfile.Service("homeassistant").Image || resources.Count != 0)
            throw new InvalidOperationException("bench.profile.unsupported");
        _ = await RunAsync(["image", "inspect", image, "--format", "{{.Id}}"], token);
        await CreateNetworkAsync(token);
        await CreateVolumeAsync(Volume, token);
        // Public synthetic configuration, only in our new named volume. The container's own
        // Python runtime starts HA; no Python installation on the developer machine is needed.
        const string bootstrap =
            "test ! -e /config/configuration.yaml && printf '%s\\n' 'homeassistant:' '  name: M0 fixture' '  latitude: 0' '  longitude: 0' '  elevation: 0' '  time_zone: Europe/Moscow' '  country: RU' 'config:' 'frontend:' 'lovelace:' 'onboarding:' 'websocket_api:' 'input_boolean:' > /config/configuration.yaml";
        var seed = session + "-seed";
        resources.Add(("container", seed));
        _ = await RunAsync(
            [
                "create",
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
                bootstrap,
            ],
            token
        );
        _ = await RunAsync(["start", seed], token);
        if ((await RunAsync(["wait", seed], token)).Trim() != "0")
            throw new InvalidOperationException("bench.ha.fixture.seed.failed");
        // Keep the image's standard /init so the HA restart service is handled by s6.
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

    public async Task RestartHomeAssistantAsync(CancellationToken token)
    {
        await VerifyOwnershipAsync("container", Container, token);
        _ = await RunAsync(["stop", "--time", "20", Container], token);
        _ = await RunAsync(["start", Container], token);
        Endpoint = await ReadBindingAsync(Container, 8123, token);
        await WaitHomeAssistantAsync(token);
    }

    /// <summary>Jellyfin peer reachable from HA on this fixture's private Docker network.</summary>
    public async Task StartHomeAssistantJellyfinPeerAsync(string image, CancellationToken token)
    {
        if (
            image != PinnedRuntimeProfile.Service("jellyfin").Image
            || Endpoint is null
            || services.Count != 0
        )
            throw new InvalidOperationException("bench.profile.unsupported");
        await VerifyOwnershipAsync("container", Container, token);
        _ = await RunAsync(["image", "inspect", image, "--format", "{{.Id}}"], token);
        var container = session + "-jellyfin";
        var volume = container + "-config";
        await CreateVolumeAsync(volume, token);
        resources.Add(("container", container));
        _ = await RunAsync(
            [
                "create",
                "--name",
                container,
                "--label",
                Label + "=" + session,
                "--network",
                Network,
                "--network-alias",
                "jellyfin",
                "--publish",
                "127.0.0.1::8096",
                "--env",
                "PUID=1000",
                "--env",
                "PGID=1000",
                "--mount",
                $"type=volume,source={volume},target=/config",
                "--tmpfs",
                "/cache:rw,uid=1000,gid=1000,mode=0755",
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
        _ = await RunAsync(["start", container], token);
        services.Add("jellyfin", (container, await ReadBindingAsync(container, 8096, token), 8096));
        await WaitMediaAsync("jellyfin", token);
    }

    public async Task WaitHomeAssistantAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        using var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = Endpoint,
            Timeout = TimeSpan.FromSeconds(3),
        };
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try
            {
                using var result = await client.GetAsync("api/onboarding", deadline.Token);
                if (result.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound)
                    return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!deadline.IsCancellationRequested) { }
            await Task.Delay(1000, deadline.Token);
        }
    }
}
