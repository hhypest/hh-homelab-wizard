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
    const string jellyfinImage = "lscr.io/linuxserver/jellyfin:12.2ubu2604-ls53";
    if (profile.Source.Tag != "v1.3.0" || profile.Source.Commit != "b05047b081f3543520582315002e359ef1d93e59" ||
        profile.Services.Single(s => s.Id == "homeassistant").Image != image || profile.Services.Single(s => s.Id == "jellyfin").Image != jellyfinImage) throw new InvalidOperationException("bench.profile.unsupported");
    bench = await OwnedDockerBench.ConnectAsync(token); await bench.StartHomeAssistantAsync(image, token);
    await bench.StartHomeAssistantJellyfinPeerAsync(jellyfinImage, token);
    Console.WriteLine("fixture: pinned HA and Jellyfin ready on an owned private network; loopback ports only");
    var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    using var jellyfinHttp = Client(bench.GetEndpoint("jellyfin"));
    var jellyfin = new JellyfinContractClient(jellyfinHttp);
    await WaitForJellyfinIdentityAsync(jellyfin, token);
    Console.WriteLine("jellyfin: public identity verified");
    await jellyfin.BootstrapAsync("wizard_m0", password, token);
    Console.WriteLine("jellyfin: synthetic administrator and pinned runtime verified");
    using var http = Client(bench.Endpoint, loseOwner: true);
    await using var ha = new HomeAssistantContractClient(http, () => new ProbeSocket());
    await Unknown(() => ha.BootstrapOwnerAsync("M0 owner", "wizard_m0", password, token), "ha.owner.result-unknown");
    if (await ha.BootstrapOwnerAsync("M0 owner", "wizard_m0", password, token)) throw new InvalidOperationException("bench.ha.owner.recreated");
    var owner = ha.OwnerId;
    if (await ha.GetProbeSessionCountAsync(token) != 1) throw new InvalidOperationException("bench.ha.session.orphaned");
    Console.WriteLine("ha: owner response loss reconciled through normal login; onboarding complete; owner/admin/version verified");
    await bench.VerifyDsmInteractiveFormsAsync(token);
    var castProgress = await VerifyDeviceFormsAsync(ha, token);
    var jellyfinTarget = bench.GetInternalEndpoint("jellyfin");
    if ((await ha.StartJellyfinSetupAsync(jellyfinTarget, jellyfin.ServerId!, "wizard_m0", token)).Stage != HomeAssistantJellyfinStage.AwaitingCredentials ||
        (await ha.SubmitJellyfinCredentialsAsync("synthetic-wrong-password", token)).Stage != HomeAssistantJellyfinStage.AuthenticationRejected)
        throw new InvalidOperationException("bench.ha.jellyfin.credentials-check.failed");
    var jellyfinProgress = await ha.SubmitJellyfinCredentialsAsync(password, token);
    using (var setupDeadline = CancellationTokenSource.CreateLinkedTokenSource(token))
    {
        setupDeadline.CancelAfter(TimeSpan.FromMinutes(2));
        while (jellyfinProgress.Stage == HomeAssistantJellyfinStage.AwaitingSetup)
        { await Task.Delay(1000, setupDeadline.Token); jellyfinProgress = await ha.ObserveJellyfinSetupAsync(setupDeadline.Token); }
    }
    if (jellyfinProgress.Stage != HomeAssistantJellyfinStage.Loaded || jellyfinProgress.EntryId is null)
        throw new InvalidOperationException("bench.ha.jellyfin.not-loaded");
    Console.WriteLine("ha: Jellyfin USER flow rejected wrong credentials; corrected input loaded pinned server; device identity verified from HA");
    using (var wrongHttp = Client(bench.Endpoint))
    await using (var wrong = new HomeAssistantContractClient(wrongHttp))
    {
        try { await wrong.LoginOwnerAsync("M0 owner", "wizard_m0", "synthetic-wrong-password", token); throw new InvalidOperationException("bench.ha.wrong-password.accepted"); }
        catch (MediaContractException e) when (e.Code == "ha.login.rejected-or-interaction-required") { }
    }
    using (var rejectedHttp = Client(bench.Endpoint))
    await using (var rejected = new HomeAssistantContractClient(rejectedHttp))
    {
        try { await rejected.LoginOwnerAsync("foreign owner", "wizard_m0", password, token); throw new InvalidOperationException("bench.ha.foreign-owner.accepted"); }
        catch (MediaContractException e) when (e.Code == "ha.owner.identity-mismatch") { }
        if (rejected.SessionCleanupFailure is not null || await ha.GetProbeSessionCountAsync(token) != 1)
            throw new InvalidOperationException("bench.ha.rejected-session.orphaned");
    }
    Console.WriteLine("ha: post-token owner rejection revoked its session; no orphan client refresh tokens");
    // A real storage helper is fixture data, not a fabricated replacement for a missing DSM sensor.
    var helper = await ha.CreateProbeHelperAsync(token);
    var observed = (await ha.GetEntitiesAsync(token)).Single(e => e.Platform == "input_boolean" && e.UniqueId == helper);
    var roleInventory = await ha.GetRoleInventoryAsync(token);
    await bench.VerifySynologyRoleSourceAsync(token);
    if (!roleInventory.Entities.Any(e => e.Identity == observed) || !roleInventory.Entries.Any(e => e.Domain == "jellyfin" && e.State == "loaded"))
        throw new InvalidOperationException("bench.ha.roles.inventory.failed");
    await ha.DisableProbeHelperAsync(observed, token);
    var disabledHelper = (await ha.GetEntitiesAsync(token)).Single(e => e.RegistryId == observed.RegistryId);
    if (disabledHelper.DisabledBy != "user" || await ha.EnsureEntityEnabledAsync(disabledHelper, token) != HomeAssistantEnableStage.RestartRequired)
        throw new InvalidOperationException("bench.ha.enable.restart-not-reported");
    await ha.DisableProbeHelperAsync(observed, token);
    ProbeSocket.LoseNext = "config/entity_registry/update";
    await Unknown(async () => { await ha.EnsureEntityEnabledAsync(disabledHelper, token); return true; }, "ha.ws.result-unknown");
    await ha.ReconnectAsync(token);
    ProbeSocket.ForbidWrites = true;
    try
    {
        if (await ha.EnsureEntityEnabledAsync(disabledHelper, token) != HomeAssistantEnableStage.Unchanged)
            throw new InvalidOperationException("bench.ha.enable.rewritten");
    }
    finally { ProbeSocket.ForbidWrites = false; }
    Console.WriteLine("ha: restricted role inventory read; selected native helper enabled; restart requirement preserved; lost enable response not repeated");
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
    using (var otherHttp = Client(bench.Endpoint))
    await using (var other = new HomeAssistantContractClient(otherHttp))
    {
        await other.LoginOwnerAsync("M0 owner", "wizard_m0", password, token);
        try { await other.StageHttpProtectionAsync(token); throw new InvalidOperationException("bench.ha.foreign-trial.adopted"); }
        catch (MediaContractException e) when (e.Code == "ha.http.pending-conflict") { }
        try { await other.ConfirmHttpProtectionAsync(token); throw new InvalidOperationException("bench.ha.foreign-trial.promoted"); }
        catch (MediaContractException e) when (e.Code == "ha.http.intent-required") { }
    }
    if (await ha.GetProbeSessionCountAsync(token) != 1) throw new InvalidOperationException("bench.ha.session.orphaned");
    Console.WriteLine("ha: separate client rejected an identical pending HTTP trial; issuing client retains reconciliation intent");
    ProbeSocket.LoseNext = "http/config/promote";
    await Unknown(() => ha.ConfirmHttpProtectionAsync(token), "ha.ws.result-unknown"); await ha.ReconnectAsync(token);
    if (await ha.ConfirmHttpProtectionAsync(token) || await ha.StageHttpProtectionAsync(token) != HomeAssistantHttpStage.Unchanged)
        throw new InvalidOperationException("bench.ha.http.rewritten");
    await ha.CheckConfigurationAsync(token);
    Console.WriteLine("ha: HTTP pending trial restarted, reconnected, verified and promoted; configure/promote response loss reconciled; check_config valid");
    ProbeSocket.ForbidWrites = true;
    ProbeHttp.ForbidFlows = true;
    if (await ha.StartDeviceSetupAsync(new(HomeAssistantDeviceIntegration.GoogleCast, null), token) != castProgress)
        throw new InvalidOperationException("bench.ha.cast.repeat.failed");
    if (await ha.StartJellyfinSetupAsync(jellyfinTarget, jellyfin.ServerId!, "wizard_m0", token) != jellyfinProgress)
        throw new InvalidOperationException("bench.ha.jellyfin.repeat.failed");
    await VerifyAsync(ha, observed, renamed, dashboard, token);
    ProbeHttp.LoseNextRevoke = true;
    await Unknown(async () => { await ha.RevokeSessionAsync(token); return true; }, "ha.session.revoke.result-unknown");
    if (ha.SessionCleanupFailure != "ha.session.revoke.result-unknown") throw new InvalidOperationException("bench.ha.revoke.failure-hidden");
    await ha.RevokeSessionAsync(token); // Explicit retry of idempotent revocation; empty 200 is valid.
    if (!ha.SessionRevocationConfirmed) throw new InvalidOperationException("bench.ha.revoke.unconfirmed");
    await ha.DisposeAsync();
    Console.WriteLine("ha: lost revocation response reported; explicit cleanup retry confirmed; no synchronous disposal");
    await bench.RestartHomeAssistantAsync(token);
    // Dynamic Docker port may change; login with the same password in a new context.
    using var restartedHttp = Client(bench.Endpoint, forbidConfiguration: true);
    await using var restarted = new HomeAssistantContractClient(restartedHttp, () => new ProbeSocket());
    if (await restarted.BootstrapOwnerAsync("M0 owner", "wizard_m0", password, token) || restarted.OwnerId != owner)
        throw new InvalidOperationException("bench.ha.owner.persistence.failed");
    await restarted.WaitUntilRunningAsync(token);
    if (await restarted.GetProbeSessionCountAsync(token) != 1) throw new InvalidOperationException("bench.ha.session.orphaned");
    await VerifyAsync(restarted, observed, renamed, dashboard, token);
    if (!await restarted.IsProbeHelperAvailableAsync(renamed, token)) throw new InvalidOperationException("bench.ha.enabled-helper.unavailable");
    Console.WriteLine("restart: enabled native helper has a live on/off state; registry enable alone was not accepted as availability");
    if (await restarted.ObserveJellyfinEntryAsync(jellyfinProgress.EntryId, jellyfin.ServerId!, token) != jellyfinProgress)
        throw new InvalidOperationException("bench.ha.jellyfin.persistence.failed");
    Console.WriteLine("restart: Jellyfin entry still loaded with the same server identity; read-only confirmation; no active playback required");
    if (await restarted.ObserveDeviceEntryAsync(HomeAssistantDeviceIntegration.GoogleCast, castProgress.EntryId!, token) != castProgress)
        throw new InvalidOperationException("bench.ha.cast.persistence.failed");
    Console.WriteLine("restart: Google Cast USER entry loaded; read-only confirmation; no physical Cast device claimed");
    Console.WriteLine("restart: same owner, registry identity, dashboard and stable HTTP policy; configuration writes blocked; no storage edits");
    await restarted.RevokeSessionAsync(token);
    if (!restarted.SessionRevocationConfirmed) throw new InvalidOperationException("bench.ha.revoke.unconfirmed");
    exit = 0;
}
catch (MediaContractException e) { Console.Error.WriteLine(e.Code + (e.StatusCode is { } s ? "; HTTP " + (int)s : "")); }
catch (Exception e) { Console.Error.WriteLine(e is InvalidOperationException && e.Message.StartsWith("bench.", StringComparison.Ordinal) ? e.Message : e is OperationCanceledException ? "bench.cancelled-or-timeout" : "bench.probe.failed"); }
finally { if (bench is not null) { await bench.DisposeAsync(); if (!bench.CleanupSucceeded) exit = 1; } }
return exit;

static HttpClient Client(Uri endpoint, bool loseOwner = false, bool forbidConfiguration = false) => new(new ProbeHttp(
    new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false, UseProxy = false }, loseOwner, forbidConfiguration))
    { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(30) };
static async Task WaitForJellyfinIdentityAsync(JellyfinContractClient jellyfin, CancellationToken token)
{
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(2));
    while (true)
    {
        try { await jellyfin.GetStartupCompletedAsync(deadline.Token); return; }
        catch (MediaContractException e) when (e.Code == "jellyfin.read.transport-failed") { }
        catch (TaskCanceledException) when (!deadline.IsCancellationRequested) { }
        await Task.Delay(1000, deadline.Token); // Only a readiness GET; no credentials or writes are repeated.
    }
}
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
static async Task<HomeAssistantDeviceProgress> VerifyDeviceFormsAsync(HomeAssistantContractClient ha, CancellationToken token)
{
    foreach (var integration in new[] { HomeAssistantDeviceIntegration.SynologyDsm, HomeAssistantDeviceIntegration.LgWebOs, HomeAssistantDeviceIntegration.AndroidTvRemote })
    {
        // Host is never submitted to DSM/Android. LG host submission only advances to a confirmation form.
        Console.WriteLine("ha: validating native initial form for " + integration);
        var progress = await ha.StartDeviceSetupAsync(new(integration, "192.0.2.1"), token);
        var expected = integration == HomeAssistantDeviceIntegration.SynologyDsm ? HomeAssistantDeviceInput.DsmCredentials : HomeAssistantDeviceInput.Host;
        if (progress.Input != expected || progress.Stage != HomeAssistantDeviceSetupStage.AwaitingInput)
            throw new InvalidOperationException("bench.ha.device.initial-form.failed");
        if (integration == HomeAssistantDeviceIntegration.LgWebOs &&
            (await ha.SubmitDeviceHostAsync(integration, token)).Input != HomeAssistantDeviceInput.LgConfirmation)
            throw new InvalidOperationException("bench.ha.lg.confirmation-form.failed");
    }
    Console.WriteLine("ha: native DSM/LG/Android USER forms validated; LG confirmation pending; no device credentials or pairing submitted");
    if ((await ha.StartDeviceSetupAsync(new(HomeAssistantDeviceIntegration.GoogleCast, null), token)).Input != HomeAssistantDeviceInput.CastHosts)
        throw new InvalidOperationException("bench.ha.cast.initial-form.failed");
    var cast = await ha.SubmitCastHostsAsync([], token);
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(2));
    while (cast.Stage == HomeAssistantDeviceSetupStage.AwaitingSetup)
    { await Task.Delay(1000, deadline.Token); cast = await ha.ObserveDeviceSetupAsync(HomeAssistantDeviceIntegration.GoogleCast, deadline.Token); }
    if (cast.Stage != HomeAssistantDeviceSetupStage.EntryLoaded || cast.EntryId is null) throw new InvalidOperationException("bench.ha.cast.not-loaded");
    Console.WriteLine("ha: native Google Cast USER flow loaded with empty discovery hints; no device selected or playback readiness claimed");
    return cast;
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
        if (ForbidWrites && current is not ("auth" or "auth/current_user" or "auth/refresh_tokens" or "get_config" or "get_states" or "config/entity_registry/list" or "config/device_registry/list" or "lovelace/dashboards/list" or "lovelace/config" or "http/config"))
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
    public static bool LoseNextRevoke { get; set; }
    public static bool ForbidFlows { get; set; }
    private bool lost;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var path = request.RequestUri!.AbsolutePath;
        if (ForbidFlows && request.Method != HttpMethod.Get && path.StartsWith("/api/config/config_entries/flow", StringComparison.Ordinal))
            throw new InvalidOperationException("bench.ha.flow-write.forbidden");
        if (forbidConfiguration && request.Method != HttpMethod.Get && path is not ("/auth/login_flow" or "/auth/token" or "/auth/revoke" or "/api/config/core/check_config") && !path.StartsWith("/auth/login_flow/", StringComparison.Ordinal))
            throw new InvalidOperationException("bench.ha.configuration-write.forbidden");
        var response = await base.SendAsync(request, token);
        if (loseOwner && !lost && path == "/api/onboarding/users" && response.IsSuccessStatusCode)
        { lost = true; response.Dispose(); throw new HttpRequestException("synthetic-response-loss"); }
        if (LoseNextRevoke && path == "/auth/revoke" && response.IsSuccessStatusCode)
        { LoseNextRevoke = false; response.Dispose(); throw new HttpRequestException("synthetic-response-loss"); }
        return response;
    }
}
