using System.Net;
using System.Security.Cryptography;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: ArrDockerProbe <research-profile.json>; local Linux Docker and four cached pinned images required.");
    return 2;
}
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(10));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var token = cancellation.Token;
OwnedDockerBench? bench = null;
var exitCode = 1;
try
{
    var images = new Dictionary<string, (string Image, int Port)>
    {
        ["qbittorrent"] = ("lscr.io/linuxserver/qbittorrent:5.2.4_v2.0.15-ls479", 9080),
        ["radarr"] = ("lscr.io/linuxserver/radarr:6.4.4.10685-ls319", 7878),
        ["sonarr"] = ("lscr.io/linuxserver/sonarr:4.0.20.3014-ls326", 8989),
        ["prowlarr"] = ("lscr.io/linuxserver/prowlarr:2.6.5.5623-ls163", 9696)
    };
    var profile = ProfileReader.Parse(await File.ReadAllTextAsync(args[0], token));
    if (profile.Source.Tag != "v1.3.0" || profile.Source.Commit != "b05047b081f3543520582315002e359ef1d93e59" ||
        images.Any(pair => profile.Services.Single(s => s.Id == pair.Key).Image != pair.Value.Image))
        throw new InvalidOperationException("bench.profile.unsupported");
    bench = await OwnedDockerBench.ConnectAsync(token);
    foreach (var (id, spec) in images) await bench.StartMediaServiceAsync(id, spec.Image, spec.Port, token);
    await bench.PrepareMediaFoldersAsync(token);
    var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    using (var http = Client(bench.GetEndpoint("qbittorrent")))
    {
        var qb = new QbittorrentContractClient(http);
        await qb.LoginAsync("admin", await bench.ReadMediaSecretAsync("qbittorrent", token), token);
        await qb.EnsureCategoryAsync("movies", "/data/torrents/movies", token);
        await qb.EnsureCategoryAsync("tv", "/data/torrents/tv", token);
        await qb.ChangeCredentialsAsync("wizard_m0", password, token);
    }
    var keys = new Dictionary<ArrService, string>();
    foreach (var service in Enum.GetValues<ArrService>())
    {
        var id = service.ToString().ToLowerInvariant();
        keys[service] = await bench.ReadMediaSecretAsync(id, token);
        using var http = Client(bench.GetEndpoint(id));
        using var ui = Client(bench.GetEndpoint(id));
        var api = new ArrContractClient(http, service, keys[service]);
        if (!await api.ConfigureAuthenticationAsync("wizard_m0", password, ui, token))
            throw new InvalidOperationException("bench.auth.initial-write.missing");
        using var wrongUi = Client(bench.GetEndpoint(id));
        try
        {
            await api.ConfigureAuthenticationAsync("wizard_m0", "synthetic-wrong-password", wrongUi, token);
            throw new InvalidOperationException("bench.auth.wrong-password.accepted");
        }
        catch (ArrContractException e) when (e.Code == "arr.ui.authentication.rejected") { }
        using var unauth = Client(bench.GetEndpoint(id));
        using var response = await unauth.GetAsync(service == ArrService.Prowlarr ? "api/v1/system/status" : "api/v3/system/status", token);
        if (response.StatusCode != HttpStatusCode.Unauthorized) throw new InvalidOperationException("bench.api.authentication-bypass");
        Console.WriteLine(id + ": version, Forms/enabled, UI credentials and API-key requirement verified");
    }
    await ConfigureLinksAsync(bench, keys, password, initial: true, injectLoss: true, token);
    await ConfigureLinksAsync(bench, keys, password, initial: false, injectLoss: false, token);
    Console.WriteLine("links: roots, movies/tv download clients and fullSync applications verified; repeat skipped; response loss reconciled");
    foreach (var id in images.Keys)
    {
        await bench.RestartMediaServiceAsync(id, token);
        Console.WriteLine(id + ": restarted; readiness observed (not an authentication proof)");
    }
    // Original keys/credentials and saved definitions must survive; no configuration writes allowed here.
    foreach (var (service, key) in keys)
    {
        var endpoint = bench.GetEndpoint(service.ToString().ToLowerInvariant());
        using var http = Client(endpoint, forbidConfigurationWrites: true); using var ui = Client(endpoint);
        if (await new ArrContractClient(http, service, key).ConfigureAuthenticationAsync("wizard_m0", password, ui, token))
            throw new InvalidOperationException("bench.auth.persistence.failed");
    }
    await ConfigureLinksAsync(bench, keys, password, initial: false, injectLoss: false, token);
    Console.WriteLine("restart: original API keys, credentials, roots and links persisted; no configuration writes");
    exitCode = 0;
}
catch (ArrContractException e) { Console.Error.WriteLine(e.Code + (e.StatusCode is { } status ? "; HTTP " + (int)status : "")); }
catch (QbittorrentContractException e) { Console.Error.WriteLine(e.Code); }
catch (Exception e)
{
    Console.Error.WriteLine(e is InvalidOperationException && e.Message.StartsWith("bench.", StringComparison.Ordinal)
        ? e.Message : e is OperationCanceledException ? "bench.cancelled-or-timeout" : "bench.probe.failed");
}
finally
{
    if (bench is not null) { await bench.DisposeAsync(); if (!bench.CleanupSucceeded) exitCode = 1; }
}
return exitCode;

static HttpClient Client(Uri endpoint, bool loseRootResponse = false, bool forbidConfigurationWrites = false)
{
    HttpMessageHandler handler = new HttpClientHandler { UseCookies = true, AllowAutoRedirect = false, UseProxy = false };
    if (loseRootResponse) handler = new LoseRootResponse(handler);
    if (forbidConfigurationWrites) handler = new ForbidConfigurationWrites(handler);
    return new HttpClient(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(20) };
}

static async Task ConfigureLinksAsync(OwnedDockerBench bench, Dictionary<ArrService, string> keys, string password, bool initial, bool injectLoss, CancellationToken token)
{
    using var prowlarrHttp = Client(bench.GetEndpoint("prowlarr"), forbidConfigurationWrites: !initial);
    var prowlarr = new ArrContractClient(prowlarrHttp, ArrService.Prowlarr, keys[ArrService.Prowlarr]);
    foreach (var service in new[] { ArrService.Radarr, ArrService.Sonarr })
    {
        var id = service.ToString().ToLowerInvariant();
        using var http = Client(bench.GetEndpoint(id), injectLoss && service == ArrService.Radarr, forbidConfigurationWrites: !initial);
        var api = new ArrContractClient(http, service, keys[service]);
        var path = service == ArrService.Radarr ? "/data/media/movies" : "/data/media/tv";
        if (injectLoss && service == ArrService.Radarr)
        {
            try { await api.EnsureRootFolderAsync(path, token); throw new InvalidOperationException("bench.loss.not-injected"); }
            catch (ArrContractException e) when (e.Code == "arr.root.result-unknown") { }
            if (await api.EnsureRootFolderAsync(path, token)) throw new InvalidOperationException("bench.root.reconciliation.failed");
        }
        else if (await api.EnsureRootFolderAsync(path, token) != initial) throw new InvalidOperationException("bench.root.repeat.failed");
        var connection = new ArrDownloadConnection("qbittorrent", bench.GetInternalEndpoint("qbittorrent").Port,
            service == ArrService.Radarr ? "movies" : "tv");
        if (await api.EnsureDownloadClientAsync(connection, "wizard_m0", password, token) != initial)
            throw new InvalidOperationException("bench.download.repeat.failed");
        try
        {
            await api.EnsureDownloadClientAsync(connection with { Host = "foreign-host" }, "wizard_m0", password, token);
            throw new InvalidOperationException("bench.download.conflict.missed");
        }
        catch (ArrContractException e) when (e.Code == "arr.provider.conflict") { }
        if (await prowlarr.EnsureApplicationAsync(service, bench.GetInternalEndpoint("prowlarr"), bench.GetInternalEndpoint(id), keys[service], token) != initial)
            throw new InvalidOperationException("bench.application.repeat.failed");
    }
}

internal sealed class LoseRootResponse(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private bool injected;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var response = await base.SendAsync(request, token);
        if (!injected && request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/rootfolder", StringComparison.Ordinal) && response.IsSuccessStatusCode)
        {
            injected = true; response.Dispose(); throw new HttpRequestException("synthetic-response-loss");
        }
        return response;
    }
}

internal sealed class ForbidConfigurationWrites(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method != HttpMethod.Get && !(request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsolutePath.EndsWith("/test", StringComparison.Ordinal)))
            throw new InvalidOperationException("bench.persisted-state.configuration-write.forbidden");
        return base.SendAsync(request, token);
    }
}
