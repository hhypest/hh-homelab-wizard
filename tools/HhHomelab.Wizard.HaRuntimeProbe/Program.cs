using System.Security.Cryptography;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

if (args.Length != 1) { Console.Error.WriteLine("Usage: HaRuntimeProbe <hh-homelab-v1.3.0-checkout>; owned local fixture only."); return 2; }
using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(12));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
var token = cancel.Token;
OwnedDockerBench? bench = null;
var exit = 1;
try
{
    var component = await MonitorDockerPackage.FetchAsync(token);
    var payload = await HomeAssistantLocalProfile.RenderAsync(args[0], new HomeAssistantLocalSettings
    {
        NasAddress = "127.0.0.1", TvBroadcastAddress = "192.0.2.255", TvMac = "02:00:00:00:00:01",
        JellyfinUserId = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), JellyfinToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))
    }, component, token);
    bench = await OwnedDockerBench.ConnectAsync(token); await bench.StartHomeAssistantProfileAsync(payload, token);
    Console.WriteLine("ha-runtime: real pinned HA/Socket Proxy started; synthetic Docker API; no host socket");
    using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
    using var http = new HttpClient(handler) { BaseAddress = bench.Endpoint };
    await using var ha = new HomeAssistantContractClient(http);
    var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    await ha.BootstrapOwnerAsync("M0 runtime owner", "wizard_m0", password, token);
    Console.WriteLine("ha-runtime: owner/onboarding verified");
    await ha.WaitUntilRunningAsync(token); Console.WriteLine("ha-runtime: HA RUNNING");
    await ha.CheckConfigurationAsync(token); Console.WriteLine("ha-runtime: check_config valid");
    await bench.VerifyDockerProxyPolicyAsync(token); Console.WriteLine("ha-runtime: proxy GET allowed, POST rejected");
    var first = await ha.ReadLocalProfileRuntimeAsync(token);
    Console.WriteLine("ha-runtime.initial: monitor=" + first.MonitorDockerLoaded + "; missing=" +
        string.Join(",", first.DockerValues.Where(v => v.Status == HomeAssistantValueStatus.Missing).Select(v => v.EntityId)));
    var observed = await WaitAsync(ha, r => r.MonitorDockerLoaded &&
        r.DockerValues.Where(v => v.EntityId.EndsWith("_memory", StringComparison.Ordinal)).All(v => v.Number == 64) &&
        r.DockerValues.Where(v => v.EntityId.EndsWith("_memory_percent", StringComparison.Ordinal)).All(v => v.Number == 25) &&
        r.DockerValues.Single(v => v.EntityId == "sensor.docker_containers_running").Number == 9 &&
        r.DockerValues.Single(v => v.EntityId == "sensor.docker_containers_total").Number == 9 && r.DockerProxyError == false, token);
    Console.WriteLine("ha-runtime: Monitor Docker loaded; synthetic memory and container counts mapped; fast sensor healthy");
    Console.WriteLine("ha-runtime: hardware observations: " + string.Join(",", observed.Hardware.Select(v => v.Status).Distinct()));
    if (observed.Hardware.Any(v => v.Status != HomeAssistantValueStatus.Missing) ||
        observed.LocalScripts.Any(v => v.Status != HomeAssistantValueStatus.Available) ||
        observed.DockerValues.Any(v => v.Status == HomeAssistantValueStatus.Missing))
        throw new InvalidOperationException("bench.ha.profile.inventory.failed");
    Console.WriteLine("ha-runtime: all local scripts and 40 Docker entities present; missing hardware remains Missing");
    Console.WriteLine("ha-runtime.cpu: " + string.Join(",", observed.DockerValues.Where(v => v.EntityId.EndsWith("_cpu", StringComparison.Ordinal)).Select(v => v.Status).Distinct()));
    await ha.NotifyLocalProfileProbeAsync(token);
    if (!await ha.HasLocalProfileProbeNotificationAsync(token)) throw new InvalidOperationException("bench.notification.missing");
    Console.WriteLine("ha-runtime: local notification delivered");
    foreach (var scenario in new[] { DockerFixtureScenario.Exited, DockerFixtureScenario.Oom, DockerFixtureScenario.Restarting,
        DockerFixtureScenario.Malformed, DockerFixtureScenario.HttpError, DockerFixtureScenario.Running })
    {
        await bench.SetDockerFixtureScenarioAsync(scenario, token); await ha.RefreshDockerDownProbeAsync(token);
        await WaitAsync(ha, r => scenario switch
        {
            DockerFixtureScenario.Exited => Down(r) == 1 && r.DockerOom == false && r.DockerProxyError == false,
            DockerFixtureScenario.Oom => Down(r) == 1 && r.DockerOom == true,
            DockerFixtureScenario.Restarting => Down(r) == 1 && r.DockerRestarting == 1,
            DockerFixtureScenario.Malformed or DockerFixtureScenario.HttpError => r.DockerProxyError == true,
            DockerFixtureScenario.Running => Down(r) == 0 && r.DockerProxyError == false,
            _ => false
        }, token);
        Console.WriteLine("ha-runtime.fast-sensor: " + scenario + " passed");
    }
    await bench.SetDockerProxyRunningAsync(false, token); await ha.RefreshDockerDownProbeAsync(token);
    await WaitAsync(ha, r => r.DockerProxyError == true, token);
    await bench.SetDockerProxyRunningAsync(true, token); await bench.VerifyDockerProxyPolicyAsync(token); await ha.RefreshDockerDownProbeAsync(token);
    await WaitAsync(ha, r => r.DockerProxyError == false, token);
    Console.WriteLine("ha-runtime: proxy interruption/recovery distinguished from healthy state");
    await bench.CheckHomeAssistantActionsAsync(token);
    await ha.RevokeSessionAsync(token);
    if (!ha.SessionRevocationConfirmed) throw new InvalidOperationException("bench.session.cleanup.failed");
    await bench.RestartHomeAssistantAsync(token);
    using var restartHandler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false };
    using var restartHttp = new HttpClient(restartHandler) { BaseAddress = bench.Endpoint };
    await using var restartHa = new HomeAssistantContractClient(restartHttp);
    await restartHa.LoginOwnerAsync("M0 runtime owner", "wizard_m0", password, token); await restartHa.WaitUntilRunningAsync(token);
    await WaitAsync(restartHa, r => r.MonitorDockerLoaded && r.DockerValues.Single(v => v.EntityId == "sensor.docker_radarr_memory").Number == 64, token);
    await restartHa.CheckConfigurationAsync(token); await restartHa.RevokeSessionAsync(token);
    if (!restartHa.SessionRevocationConfirmed) throw new InvalidOperationException("bench.session.cleanup.failed");
    Console.WriteLine("ha-runtime: restart persisted full profile; session revocation confirmed");
    exit = 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("ha-runtime: cancelled or deadline exceeded"); }
catch (MediaContractException e) { Console.Error.WriteLine("ha-runtime: " + e.Code); }
catch (Exception) { Console.Error.WriteLine("ha-runtime: failed; raw errors suppressed"); }
finally { if (bench is not null) { await bench.DisposeAsync(); if (!bench.CleanupSucceeded) exit = 1; } }
return exit;

static double? Down(HomeAssistantRuntimeObservation observation) => observation.DockerValues.Single(v => v.EntityId == "sensor.docker_down").Number;
static async Task<HomeAssistantRuntimeObservation> WaitAsync(HomeAssistantContractClient client,
    Func<HomeAssistantRuntimeObservation, bool> predicate, CancellationToken token)
{
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(90));
    while (true)
    {
        var result = await client.ReadLocalProfileRuntimeAsync(deadline.Token);
        if (deadline.Token.IsCancellationRequested) throw new OperationCanceledException(deadline.Token);
        if (predicate(result)) return result;
        await Task.Delay(500, deadline.Token);
    }
}
