using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

if (args.Length != 1) { Console.Error.WriteLine("Usage: HaDockerProbe <research-profile.json>; owned local Linux Docker fixture only."); return 2; }
using var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(12));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
var token = cancel.Token;
OwnedDockerBench? bench = null;
var exit = 1;
try
{
    var profile = ProfileReader.Parse(await File.ReadAllTextAsync(args[0], token));
    const string image = "ghcr.io/home-assistant/home-assistant:2026.10.0";
    if (profile.Source.Tag != "v1.3.0" || profile.Source.Commit != "b05047b081f3543520582315002e359ef1d93e59" ||
        profile.Services.Single(s => s.Id == "homeassistant").Image != image) throw new InvalidOperationException("bench.profile.unsupported");
    bench = await OwnedDockerBench.ConnectAsync(token); await bench.StartHomeAssistantAsync(image, token);
    var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    using var http = Client(bench.Endpoint, loseOwner: true);
    using var ha = new HomeAssistantContractClient(http, () => new ProbeSocket());
    await Unknown(() => ha.BootstrapOwnerAsync("M0 owner", "wizard_m0", password, token), "ha.owner.result-unknown");
    if (await ha.BootstrapOwnerAsync("M0 owner", "wizard_m0", password, token)) throw new InvalidOperationException("bench.ha.owner.recreated");
    var owner = ha.OwnerId;
    Console.WriteLine("ha: owner response loss reconciled through normal login; onboarding complete; owner/admin/version verified");
    using (var wrongHttp = Client(bench.Endpoint))
    using (var wrong = new HomeAssistantContractClient(wrongHttp))
    {
        try { await wrong.LoginOwnerAsync("M0 owner", "wizard_m0", "synthetic-wrong-password", token); throw new InvalidOperationException("bench.ha.wrong-password.accepted"); }
        catch (MediaContractException e) when (e.Code == "ha.login.rejected-or-interaction-required") { }
    }
    // A real storage helper is fixture data, not a fabricated replacement for a missing DSM sensor.
    var helper = await ha.CreateProbeHelperAsync(token);
    var observed = (await ha.GetEntitiesAsync(token)).Single(e => e.Platform == "input_boolean" && e.UniqueId == helper);
    const string renamed = "input_boolean.hh_homelab_m0_verified";
    ProbeSocket.LoseNext = "config/entity_registry/update";
    await Unknown(() => ha.EnsureEntityIdAsync(observed, renamed, token), "ha.ws.result-unknown");
    await ha.ReconnectAsync(token);
    if (await ha.EnsureEntityIdAsync(observed, renamed, token)) throw new InvalidOperationException("bench.ha.entity.rewritten");
    try { await ha.EnsureEntityIdAsync(observed with { UniqueId = "foreign" }, renamed, token); throw new InvalidOperationException("bench.ha.conflict.missed"); }
    catch (MediaContractException e) when (e.Code == "ha.entity.conflict") { }
    var dashboard = new HomeAssistantDashboard("hh-homelab-m0", "M0 infrastructure", [renamed]);
    ProbeSocket.LoseNext = "lovelace/dashboards/create";
    await Unknown(() => ha.EnsureDashboardAsync(dashboard, token), "ha.ws.result-unknown"); await ha.ReconnectAsync(token);
    ProbeSocket.LoseNext = "lovelace/config/save";
    await Unknown(() => ha.EnsureDashboardAsync(dashboard, token), "ha.ws.result-unknown"); await ha.ReconnectAsync(token);
    if (await ha.EnsureDashboardAsync(dashboard, token)) throw new InvalidOperationException("bench.ha.dashboard.rewritten");
    Console.WriteLine("ha: registry identity and renamed ID verified; dashboard create/save response loss reconciled; conflicts preserved");
    await ha.WaitUntilRunningAsync(token);
    ProbeSocket.LoseNext = "http/config/configure";
    await Unknown(async () => { await ha.StageHttpProtectionAsync(token); return true; }, "ha.ws.result-unknown");
    await ReconnectUntilTrialAsync(ha, token);
    ProbeSocket.LoseNext = "http/config/promote";
    await Unknown(() => ha.ConfirmHttpProtectionAsync(token), "ha.ws.result-unknown"); await ha.ReconnectAsync(token);
    if (await ha.ConfirmHttpProtectionAsync(token) || await ha.StageHttpProtectionAsync(token) != HomeAssistantHttpStage.Unchanged)
        throw new InvalidOperationException("bench.ha.http.rewritten");
    await ha.CheckConfigurationAsync(token);
    Console.WriteLine("ha: HTTP pending trial restarted, reconnected, verified and promoted; configure/promote response loss reconciled; check_config valid");
    ProbeSocket.ForbidWrites = true;
    await VerifyAsync(ha, observed, renamed, dashboard, token);
    await bench.RestartHomeAssistantAsync(token);
    // Dynamic Docker port may change; login with the same password in a new context.
    using var restartedHttp = Client(bench.Endpoint, forbidConfiguration: true);
    using var restarted = new HomeAssistantContractClient(restartedHttp, () => new ProbeSocket());
    if (await restarted.BootstrapOwnerAsync("M0 owner", "wizard_m0", password, token) || restarted.OwnerId != owner)
        throw new InvalidOperationException("bench.ha.owner.persistence.failed");
    await restarted.WaitUntilRunningAsync(token);
    await VerifyAsync(restarted, observed, renamed, dashboard, token);
    Console.WriteLine("restart: same owner, registry identity, dashboard and stable HTTP policy; configuration writes blocked; no storage edits");
    exit = 0;
}
catch (MediaContractException e) { Console.Error.WriteLine(e.Code + (e.StatusCode is { } s ? "; HTTP " + (int)s : "")); }
catch (Exception e) { Console.Error.WriteLine(e is InvalidOperationException && e.Message.StartsWith("bench.", StringComparison.Ordinal) ? e.Message : e is OperationCanceledException ? "bench.cancelled-or-timeout" : "bench.probe.failed"); }
finally { if (bench is not null) { await bench.DisposeAsync(); if (!bench.CleanupSucceeded) exit = 1; } }
return exit;

static HttpClient Client(Uri endpoint, bool loseOwner = false, bool forbidConfiguration = false) => new(new ProbeHttp(
    new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false }, loseOwner, forbidConfiguration))
    { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(30) };
static async Task Unknown(Func<Task<bool>> action, string code)
{
    try { await action(); throw new InvalidOperationException("bench.response-loss.not-injected"); }
    catch (MediaContractException e) when (e.Code == code) { }
}
static async Task VerifyAsync(HomeAssistantContractClient ha, HomeAssistantEntity identity, string target, HomeAssistantDashboard dashboard, CancellationToken token)
{
    if (await ha.EnsureEntityIdAsync(identity, target, token) || await ha.EnsureDashboardAsync(dashboard, token) ||
        await ha.StageHttpProtectionAsync(token) != HomeAssistantHttpStage.Unchanged || await ha.ConfirmHttpProtectionAsync(token))
        throw new InvalidOperationException("bench.ha.persistence.failed");
    await ha.CheckConfigurationAsync(token);
}
static async Task ReconnectUntilTrialAsync(HomeAssistantContractClient ha, CancellationToken token)
{
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(2));
    while (true)
    {
        await Task.Delay(1000, deadline.Token);
        try
        {
            await ha.ReconnectAsync(deadline.Token);
            await ha.WaitUntilRunningAsync(deadline.Token);
            if (await ha.StageHttpProtectionAsync(deadline.Token) != HomeAssistantHttpStage.AwaitingVerification) throw new InvalidOperationException("bench.ha.pending.missing");
            // Confirmation below also requires a running HA and active pending trial.
            return;
        }
        catch (MediaContractException e) when (e.Code is "ha.ws.version-unavailable" or "ha.ws.authentication-failed" or "ha.ws.transport-failed" or "ha.token.transport-failed" or "ha.token.result-unknown" or "ha.token.http-error") { }
    }
}
internal sealed class ProbeSocket : IHomeAssistantSocket
{
    private readonly HomeAssistantSocket inner = new();
    private string? current;
    public static string? LoseNext { get; set; }
    public static bool ForbidWrites { get; set; }
    public Task ConnectAsync(Uri endpoint, CancellationToken token) => inner.ConnectAsync(endpoint, token);
    public async Task SendAsync(JsonObject message, CancellationToken token)
    {
        current = message["type"]!.GetValue<string>();
        if (ForbidWrites && current is not ("auth" or "auth/current_user" or "get_config" or "config/entity_registry/list" or "lovelace/dashboards/list" or "lovelace/config" or "http/config"))
            throw new InvalidOperationException("bench.ha.configuration-write.forbidden");
        await inner.SendAsync(message, token);
    }
    public async Task<JsonObject> ReceiveAsync(CancellationToken token)
    {
        var answer = await inner.ReceiveAsync(token);
        if (current == LoseNext && current is not null && answer["type"]?.GetValue<string>() == "result" && answer["success"]?.GetValue<bool>() == true)
        { LoseNext = null; throw new HttpRequestException("synthetic-response-loss"); }
        return answer;
    }
    public void Dispose() => inner.Dispose();
}
internal sealed class ProbeHttp(HttpMessageHandler inner, bool loseOwner, bool forbidConfiguration) : DelegatingHandler(inner)
{
    private bool lost;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (forbidConfiguration && request.Method != HttpMethod.Get && path is not ("/auth/login_flow" or "/auth/token" or "/api/config/core/check_config") && !path.StartsWith("/auth/login_flow/", StringComparison.Ordinal))
            throw new InvalidOperationException("bench.ha.configuration-write.forbidden");
        var response = await base.SendAsync(request, token);
        if (loseOwner && !lost && path == "/api/onboarding/users" && response.IsSuccessStatusCode)
        { lost = true; response.Dispose(); throw new HttpRequestException("synthetic-response-loss"); }
        return response;
    }
}
