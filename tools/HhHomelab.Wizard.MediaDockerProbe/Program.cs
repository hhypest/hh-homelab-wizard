using System.Net;
using System.Security.Cryptography;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

if (args.Length != 1)
{
    Console.Error.WriteLine(
        "Usage: MediaDockerProbe <research-profile.json>; local Linux Docker with volume subpaths and four cached pinned images required."
    );
    return 2;
}
using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(12));
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
var token = cancellation.Token;
OwnedDockerBench? bench = null;
var exitCode = 1;
try
{
    var images = new Dictionary<string, (string Image, int Port)>
    {
        ["radarr"] = (PinnedRuntimeProfile.Service("radarr").Image, 7878),
        ["sonarr"] = (PinnedRuntimeProfile.Service("sonarr").Image, 8989),
        ["jellyfin"] = (PinnedRuntimeProfile.Service("jellyfin").Image, 8096),
        ["seerr"] = (PinnedRuntimeProfile.Service("seerr").Image, 5055),
    };
    var profile = ProfileReader.Parse(await File.ReadAllTextAsync(args[0], token));
    PinnedRuntimeProfile.EnsureCompatible(profile);
    bench = await OwnedDockerBench.ConnectAsync(token);
    var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    var keys = new Dictionary<ArrService, string>();
    foreach (var service in new[] { ArrService.Radarr, ArrService.Sonarr })
    {
        var id = service.ToString().ToLowerInvariant();
        await bench.StartMediaServiceAsync(id, images[id].Image, images[id].Port, token);
        if (service == ArrService.Radarr)
            await bench.PrepareMediaFoldersAsync(token);
        keys[service] = await bench.ReadMediaSecretAsync(id, token);
        using var http = Client(bench.GetEndpoint(id));
        using var ui = Client(bench.GetEndpoint(id));
        var arr = new ArrContractClient(http, service, keys[service]);
        await arr.ConfigureAuthenticationAsync("wizard_m0", password, ui, token);
        await arr.EnsureRootFolderAsync(
            service == ArrService.Radarr ? "/data/media/movies" : "/data/media/tv",
            token
        );
    }
    await bench.StartMediaServiceAsync("jellyfin", images["jellyfin"].Image, 8096, token);
    await bench.StartMediaServiceAsync("seerr", images["seerr"].Image, 5055, token);
    Console.WriteLine(
        "fixture: four pinned services ready; library mount read-only; no host directories"
    );
    var libraries = new[]
    {
        new MediaLibrary("Фильмы", "movies", "/data/media/movies"),
        new MediaLibrary("Сериалы", "tvshows", "/data/media/tv"),
    };
    JellyfinLibraryIdentity[] identities;
    string serverId;
    string userId;
    using (
        var http = Client(
            bench.GetEndpoint("jellyfin"),
            loseRoutes: ["/Startup/User", "/Library/VirtualFolders"]
        )
    )
    {
        var jf = new JellyfinContractClient(http);
        await ExpectUnknownAsync(
            () => jf.BootstrapAsync("wizard_m0", password, token),
            "jellyfin.startup-user.result-unknown"
        );
        if (!await jf.BootstrapAsync("wizard_m0", password, token))
            throw new InvalidOperationException("bench.jellyfin.bootstrap.missing");
        await ExpectUnknownAsync(
            () => jf.EnsureLibraryAsync(libraries[0], token),
            "jellyfin.library.result-unknown"
        );
        if (
            await jf.EnsureLibraryAsync(libraries[0], token)
            || !await jf.EnsureLibraryAsync(libraries[1], token)
        )
            throw new InvalidOperationException("bench.jellyfin.library.reconciliation.failed");
        identities = (await jf.GetLibrariesAsync(token)).ToArray();
        serverId = jf.ServerId!;
        userId = jf.AdministratorId!;
        try
        {
            await jf.EnsureLibraryAsync(libraries[0] with { Path = "/data/media/foreign" }, token);
            throw new InvalidOperationException("bench.jellyfin.conflict.missed");
        }
        catch (MediaContractException e) when (e.Code == "jellyfin.library.conflict") { }
    }
    Console.WriteLine(
        "jellyfin: startup/user and library response loss reconciled; two libraries verified; conflict preserved"
    );
    using (var http = Client(bench.GetEndpoint("seerr"), loseRoutes: ["/api/v1/auth/jellyfin"]))
    {
        var seerr = new SeerrContractClient(http);
        await ExpectUnknownAsync(
            () =>
                seerr.LoginJellyfinAsync(
                    bench.GetInternalEndpoint("jellyfin"),
                    serverId,
                    userId,
                    "wizard_m0",
                    password,
                    token
                ),
            "seerr.login.result-unknown"
        );
    }
    // New cookie context observes the configured media-server type and omits hostname on login.
    await ConfigureSeerrAsync(
        bench,
        keys,
        identities,
        serverId,
        userId,
        password,
        initial: true,
        injectLoss: true,
        token
    );
    await VerifyExistingAsync(
        bench,
        keys,
        libraries,
        identities,
        serverId,
        userId,
        password,
        token
    );
    Console.WriteLine(
        "seerr: saved Jellyfin access, enabled library IDs, Arr Test/profile/root, initialization and repeat verified"
    );
    using (var wrong = Client(bench.GetEndpoint("jellyfin")))
    {
        try
        {
            await new JellyfinContractClient(wrong).LoginAdministratorAsync(
                "wizard_m0",
                "synthetic-wrong-password",
                token
            );
            throw new InvalidOperationException("bench.jellyfin.wrong-password.accepted");
        }
        catch (MediaContractException e)
            when (e.Code == "jellyfin.login.http-error"
                && e.StatusCode == HttpStatusCode.Unauthorized
            )
        { }
    }
    using (var wrong = Client(bench.GetEndpoint("seerr")))
    {
        try
        {
            await new SeerrContractClient(wrong).LoginJellyfinAsync(
                bench.GetInternalEndpoint("jellyfin"),
                serverId,
                userId,
                "wizard_m0",
                "synthetic-wrong-password",
                token
            );
            throw new InvalidOperationException("bench.seerr.wrong-password.accepted");
        }
        catch (MediaContractException e)
            when (e.Code == "seerr.login.http-error" && e.StatusCode == HttpStatusCode.Unauthorized)
        { }
    }
    Console.WriteLine("credentials: wrong passwords rejected in fresh Jellyfin and Seerr contexts");
    foreach (var id in images.Keys)
    {
        await bench.RestartMediaServiceAsync(id, token);
        Console.WriteLine(id + ": restarted");
    }
    await VerifyExistingAsync(
        bench,
        keys,
        libraries,
        identities,
        serverId,
        userId,
        password,
        token
    );
    Console.WriteLine(
        "restart: identities, credentials, library IDs, Arr links and initialization persisted; configuration writes blocked"
    );
    exitCode = 0;
}
catch (MediaContractException e)
{
    Console.Error.WriteLine(e.Code + (e.StatusCode is { } status ? "; HTTP " + (int)status : ""));
}
catch (ArrContractException e)
{
    Console.Error.WriteLine(e.Code);
}
catch (Exception e)
{
    Console.Error.WriteLine(
        e is InvalidOperationException && e.Message.StartsWith("bench.", StringComparison.Ordinal)
            ? e.Message
        : e is OperationCanceledException ? "bench.cancelled-or-timeout"
        : "bench.probe.failed"
    );
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
return exitCode;

static HttpClient Client(Uri endpoint, bool forbidWrites = false, string[]? loseRoutes = null)
{
    HttpMessageHandler handler = new HttpClientHandler
    {
        UseCookies = true,
        AllowAutoRedirect = false,
        UseProxy = false,
    };
    handler = new FailureLocation(handler);
    if (loseRoutes is not null)
        handler = new LoseResponses(handler, loseRoutes);
    if (forbidWrites)
        handler = new ForbidWrites(handler);
    return new HttpClient(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(30) };
}
static async Task ExpectUnknownAsync(Func<Task<bool>> operation, string code)
{
    try
    {
        await operation();
        throw new InvalidOperationException("bench.response-loss.not-injected");
    }
    catch (MediaContractException e) when (e.Code == code) { }
}
static async Task ConfigureSeerrAsync(
    OwnedDockerBench bench,
    Dictionary<ArrService, string> keys,
    JellyfinLibraryIdentity[] identities,
    string serverId,
    string userId,
    string password,
    bool initial,
    bool injectLoss,
    CancellationToken token
)
{
    using var http = Client(
        bench.GetEndpoint("seerr"),
        forbidWrites: !initial,
        loseRoutes: injectLoss ? ["/api/v1/settings/radarr", "/api/v1/settings/initialize"] : null
    );
    var seerr = new SeerrContractClient(http);
    if (
        await seerr.LoginJellyfinAsync(
            bench.GetInternalEndpoint("jellyfin"),
            serverId,
            userId,
            "wizard_m0",
            password,
            token
        )
    )
        throw new InvalidOperationException("bench.seerr.login.recreated");
    if (await seerr.EnsureLibrariesAsync(identities, token) != initial)
        throw new InvalidOperationException("bench.seerr.libraries.repeat.failed");
    foreach (var (service, key) in keys)
    {
        var endpoint = bench.GetInternalEndpoint(service.ToString().ToLowerInvariant());
        var profiles = await seerr.GetQualityProfilesAsync(service, endpoint, key, token);
        // Explicit choice for this synthetic fixture only. Product selection belongs to the user.
        var quality = profiles.Single(p => p.Name == "HD-1080p");
        var desired = new SeerrArrConnection(
            service,
            endpoint,
            quality.Id,
            quality.Name,
            service == ArrService.Radarr ? "/data/media/movies" : "/data/media/tv"
        );
        if (injectLoss && service == ArrService.Radarr)
        {
            await ExpectUnknownAsync(
                () => seerr.EnsureArrAsync(desired, key, token),
                "seerr.arr.result-unknown"
            );
            if (await seerr.EnsureArrAsync(desired, key, token))
                throw new InvalidOperationException("bench.seerr.arr.reconciliation.failed");
        }
        else if (await seerr.EnsureArrAsync(desired, key, token) != initial)
            throw new InvalidOperationException("bench.seerr.arr.repeat.failed");
        try
        {
            await seerr.EnsureArrAsync(desired with { ProfileId = 999999 }, key, token);
            throw new InvalidOperationException("bench.seerr.conflict.missed");
        }
        catch (MediaContractException e) when (e.Code == "seerr.arr.conflict") { }
        if (await seerr.EnsureArrAsync(desired, key, token))
            throw new InvalidOperationException("bench.seerr.conflict.state-changed");
    }
    if (injectLoss)
    {
        await ExpectUnknownAsync(
            () => seerr.CompleteSetupAsync(token),
            "seerr.initialize.result-unknown"
        );
        if (await seerr.CompleteSetupAsync(token))
            throw new InvalidOperationException("bench.seerr.initialize.reconciliation.failed");
    }
    else if (await seerr.CompleteSetupAsync(token) != initial)
        throw new InvalidOperationException("bench.seerr.initialize.repeat.failed");
}
static async Task VerifyExistingAsync(
    OwnedDockerBench bench,
    Dictionary<ArrService, string> keys,
    MediaLibrary[] libraries,
    JellyfinLibraryIdentity[] identities,
    string serverId,
    string userId,
    string password,
    CancellationToken token
)
{
    using var http = Client(bench.GetEndpoint("jellyfin"), forbidWrites: true);
    var jf = new JellyfinContractClient(http);
    if (
        await jf.BootstrapAsync("wizard_m0", password, token)
        || jf.ServerId != serverId
        || jf.AdministratorId != userId
    )
        throw new InvalidOperationException("bench.jellyfin.persistence.failed");
    foreach (var library in libraries)
        if (await jf.EnsureLibraryAsync(library, token))
            throw new InvalidOperationException("bench.jellyfin.library.persistence.failed");
    var saved = await jf.GetLibrariesAsync(token);
    if (saved.Count != identities.Length || identities.Any(i => !saved.Contains(i)))
        throw new InvalidOperationException("bench.jellyfin.identities.changed");
    await ConfigureSeerrAsync(
        bench,
        keys,
        identities,
        serverId,
        userId,
        password,
        initial: false,
        injectLoss: false,
        token
    );
}

internal sealed class LoseResponses(HttpMessageHandler inner, string[] routes)
    : DelegatingHandler(inner)
{
    private readonly HashSet<string> remaining = new(routes, StringComparer.Ordinal);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken token
    )
    {
        var response = await base.SendAsync(request, token);
        if (
            request.Method == HttpMethod.Post
            && response.IsSuccessStatusCode
            && remaining.Remove(request.RequestUri!.AbsolutePath)
        )
        {
            response.Dispose();
            throw new HttpRequestException("synthetic-response-loss");
        }
        return response;
    }
}

internal sealed class FailureLocation(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken token
    )
    {
        var response = await base.SendAsync(request, token);
        // Only our fixed API path and status; no headers, query, payload, body or exception text.
        if (!response.IsSuccessStatusCode)
            Console.WriteLine(
                "probe.http: " + request.RequestUri!.AbsolutePath + "; " + (int)response.StatusCode
            );
        return response;
    }
}

internal sealed class ForbidWrites(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken token
    )
    {
        var path = request.RequestUri!.AbsolutePath;
        var isAllowedPost =
            request.Method == HttpMethod.Post
            && (
                path == "/Users/AuthenticateByName"
                || path == "/api/v1/auth/jellyfin"
                || path.EndsWith("/test", StringComparison.Ordinal)
            );
        if (request.Method != HttpMethod.Get && !isAllowedPost)
            throw new InvalidOperationException(
                "bench.persisted-state.configuration-write.forbidden"
            );
        return base.SendAsync(request, token);
    }
}
