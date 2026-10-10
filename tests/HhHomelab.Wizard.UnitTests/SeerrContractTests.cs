using System.Net;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class SeerrContractTests
{
    private const string Server = "00000000000000000000000000000001";
    private const string User = "00000000000000000000000000000002";
    private const string Key = "11111111111111111111111111111111";
    private static Uri Jellyfin => new("http://jellyfin:8096/");
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static JellyfinLibraryIdentity[] Libraries => [new("00000000000000000000000000000003", "Фильмы", "movies"), new("00000000000000000000000000000004", "Сериалы", "tvshows")];
    private static SeerrArrConnection Connection(ArrService service = ArrService.Radarr) => new(service, new(service == ArrService.Radarr ? "http://radarr:7878/" : "http://sonarr:8989/"), 4, "HD-1080p", service == ArrService.Radarr ? "/data/media/movies" : "/data/media/tv");
    private static Task<bool> Login(SeerrContractClient seerr) => seerr.LoginJellyfinAsync(Jellyfin, Server, User, "wizard", "synthetic-password", Token);

    [Fact]
    public async Task LostFirstLoginIsReconciledWithFreshContextAndNoHostname()
    {
        using var f = new Fixture { LoseRoute = "/api/v1/auth/jellyfin" }; using var first = f.Client();
        var e = await Assert.ThrowsAsync<MediaContractException>(() => Login(new(first))); Assert.Equal("seerr.login.result-unknown", e.Code);
        using var next = f.Client(); Assert.False(await Login(new(next))); Assert.Equal(1, f.OwnerCreates); Assert.Equal(1, f.HostnameSubmissions);
    }

    [Fact]
    public async Task ExistingCookieSessionCannotProvePassword()
    {
        using var f = new Fixture(); using var h = f.Client(); var seerr = new SeerrContractClient(h); await Login(seerr);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => Login(seerr)); Assert.Equal("seerr.authentication.context-preauthenticated", e.Code); Assert.Equal(1, f.Logins);
        e = await Assert.ThrowsAsync<MediaContractException>(() => seerr.EnsureLibrariesAsync(Libraries, Token)); Assert.Equal("seerr.session.required", e.Code);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForeignMediaServerOrOwnerIsRejected(bool wrongType)
    {
        using var f = new Fixture { ForeignType = wrongType, WrongOwner = !wrongType }; using var h = f.Client();
        var e = await Assert.ThrowsAsync<MediaContractException>(() => Login(new(h)));
        Assert.Equal(wrongType ? "seerr.jellyfin.conflict" : "seerr.login.identity-mismatch", e.Code);
    }

    [Fact]
    public async Task LibrariesSyncOnceThenEnableSelectedIdsAndSkipRepeat()
    {
        using var f = new Fixture(); using var h = f.Client(); var seerr = new SeerrContractClient(h); await Login(seerr);
        Assert.True(await seerr.EnsureLibrariesAsync(Libraries, Token)); Assert.False(await seerr.EnsureLibrariesAsync(Libraries, Token)); Assert.Equal(1, f.Syncs); Assert.Equal(2, f.Enables);
    }

    [Fact]
    public async Task LostLibrarySyncIsObservedInsteadOfResubmitted()
    {
        using var f = new Fixture { LoseRoute = "/api/v1/settings/jellyfin/library/sync" }; using var h = f.Client(); var seerr = new SeerrContractClient(h); await Login(seerr);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => seerr.EnsureLibrariesAsync(Libraries, Token)); Assert.Equal("seerr.library-sync.result-unknown", e.Code);
        Assert.True(await seerr.EnsureLibrariesAsync(Libraries, Token)); Assert.Equal(1, f.Syncs);
    }

    [Fact]
    public async Task ForeignLibraryIdsCannotBeEnabled()
    {
        using var f = new Fixture(); using var h = f.Client(); var seerr = new SeerrContractClient(h); await Login(seerr);
        f.LibraryList.Add(new JsonObject { ["id"] = "foreign", ["name"] = "other", ["type"] = "movie", ["enabled"] = false });
        var e = await Assert.ThrowsAsync<MediaContractException>(() => seerr.EnsureLibrariesAsync(Libraries, Token)); Assert.Equal("seerr.libraries.conflict", e.Code); Assert.Equal(0, f.Enables); Assert.Equal(0, f.Syncs);
    }

    [Fact]
    public async Task LibraryHttpSuccessRequiresEnabledReadback()
    {
        using var f = new Fixture { IgnoreEnables = true }; using var h = f.Client(); var seerr = new SeerrContractClient(h); await Login(seerr);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => seerr.EnsureLibrariesAsync(Libraries, Token)); Assert.Equal("seerr.libraries.not-enabled", e.Code);
    }

    [Theory]
    [InlineData(ArrService.Radarr)]
    [InlineData(ArrService.Sonarr)]
    public async Task ArrConnectionTestsBeforeSaveAndAfterSaveAndOnRepeat(ArrService service)
    {
        using var f = new Fixture(); using var h = f.Client(); var seerr = new SeerrContractClient(h); await Login(seerr);
        Assert.True(await seerr.EnsureArrAsync(Connection(service), Key, Token)); Assert.False(await seerr.EnsureArrAsync(Connection(service), Key, Token)); Assert.Equal(1, f.ArrCreates); Assert.Equal(3, f.Tests);
    }

    [Fact]
    public async Task LostArrCreateResponseRequiresObservedStateAndNoDuplicate()
    {
        using var f = new Fixture { LoseRoute = "/api/v1/settings/radarr" }; using var h = f.Client(); var seerr = new SeerrContractClient(h); await Login(seerr);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => seerr.EnsureArrAsync(Connection(), Key, Token)); Assert.Equal("seerr.arr.result-unknown", e.Code);
        Assert.False(await seerr.EnsureArrAsync(Connection(), Key, Token)); Assert.Equal(1, f.ArrCreates);
    }

    [Fact]
    public async Task CancelledArrWriteCanBeObservedButIsNeverRetriedAutomatically()
    {
        using var f = new Fixture { CancelArrWrite = true }; using var h = f.Client(); var seerr = new SeerrContractClient(h); await Login(seerr);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => seerr.EnsureArrAsync(Connection(), Key, Token));
        Assert.False(await seerr.EnsureArrAsync(Connection(), Key, Token)); Assert.Equal(1, f.ArrCreates);
    }

    [Theory]
    [InlineData("hostname")]
    [InlineData("activeProfileId")]
    [InlineData("activeDirectory")]
    [InlineData("isDefault")]
    public async Task ExistingArrConflictCannotClearDefaultsOrOverwriteSettings(string field)
    {
        using var f = new Fixture(); using var h = f.Client(); var seerr = new SeerrContractClient(h); await Login(seerr);
        await seerr.EnsureArrAsync(Connection(), Key, Token); f.Arr[ArrService.Radarr][0]![field] = "foreign-value";
        var e = await Assert.ThrowsAsync<MediaContractException>(() => seerr.EnsureArrAsync(Connection(), Key, Token)); Assert.Equal("seerr.arr.conflict", e.Code); Assert.Equal(1, f.ArrCreates);
    }

    [Fact]
    public async Task WrongProfileOrRootIsRejectedBeforeSaveEvenWhenTestHttpSucceeds()
    {
        using var f = new Fixture(); using var h = f.Client(); var seerr = new SeerrContractClient(h); await Login(seerr);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => seerr.EnsureArrAsync(Connection() with { ProfileId = 99 }, Key, Token)); Assert.Equal("seerr.arr-test.selection-missing", e.Code);
        e = await Assert.ThrowsAsync<MediaContractException>(() => seerr.EnsureArrAsync(Connection() with { RootFolder = "/data/media/foreign" }, Key, Token)); Assert.Equal("seerr.arr-test.selection-missing", e.Code); Assert.Equal(0, f.ArrCreates);
    }

    [Fact]
    public async Task FailedConnectionTestCannotSaveOrInitialize()
    {
        using var f = new Fixture { RejectTests = true }; using var h = f.Client(); var seerr = new SeerrContractClient(h); await Login(seerr);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => seerr.EnsureArrAsync(Connection(), Key, Token)); Assert.Equal("seerr.arr-test.http-error", e.Code); Assert.Equal(0, f.ArrCreates);
        e = await Assert.ThrowsAsync<MediaContractException>(() => seerr.CompleteSetupAsync(Token)); Assert.Equal("seerr.initialize.prerequisites-missing", e.Code); Assert.Equal(0, f.Initializes);
    }

    [Fact]
    public async Task LostInitializeResponseIsObservedWithoutSecondPost()
    {
        using var f = new Fixture { LoseRoute = "/api/v1/settings/initialize" }; using var h = f.Client(); var seerr = new SeerrContractClient(h); await Ready(seerr);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => seerr.CompleteSetupAsync(Token)); Assert.Equal("seerr.initialize.result-unknown", e.Code);
        Assert.False(await seerr.CompleteSetupAsync(Token)); Assert.Equal(1, f.Initializes);
    }

    [Fact]
    public async Task InitializeRechecksLaterLibraryChanges()
    {
        using var f = new Fixture(); using var h = f.Client(); var seerr = new SeerrContractClient(h); await Ready(seerr);
        f.LibraryList[0]!["enabled"] = false;
        var e = await Assert.ThrowsAsync<MediaContractException>(() => seerr.CompleteSetupAsync(Token)); Assert.Equal("seerr.libraries.not-enabled", e.Code); Assert.Equal(0, f.Initializes);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task LoginErrorsAreSanitizedAndPreserveStatus(int status)
    {
        using var f = new Fixture { LoginStatus = (HttpStatusCode)status }; using var h = f.Client();
        var e = await Assert.ThrowsAsync<MediaContractException>(() => Login(new(h))); Assert.Equal("seerr.login.http-error", e.Message); Assert.Equal((HttpStatusCode)status, e.StatusCode);
    }

    [Fact]
    public async Task UnsupportedVersionNeverSubmitsCredentials()
    {
        using var f = new Fixture { Version = "3.6.0" }; using var h = f.Client();
        var e = await Assert.ThrowsAsync<MediaContractException>(() => Login(new(h))); Assert.Equal("seerr.version.unsupported", e.Code); Assert.Equal(0, f.Logins);
    }

    [Fact]
    public async Task FailedJellyfinConnectionCannotLeaveSessionVerified()
    {
        using var f = new Fixture { RejectJellyfinConnection = true }; using var h = f.Client(); var seerr = new SeerrContractClient(h);
        await Assert.ThrowsAsync<MediaContractException>(() => Login(seerr));
        var e = await Assert.ThrowsAsync<MediaContractException>(() => seerr.EnsureArrAsync(Connection(), Key, Token)); Assert.Equal("seerr.session.required", e.Code);
    }

    [Fact]
    public async Task FailedArrVerificationRetiresReadinessUntilConnectionIsObservedAgain()
    {
        using var f = new Fixture(); using var h = f.Client(); var seerr = new SeerrContractClient(h); await Ready(seerr);
        await Assert.ThrowsAsync<MediaContractException>(() => seerr.EnsureArrAsync(Connection() with { ProfileId = 99 }, Key, Token));
        var e = await Assert.ThrowsAsync<MediaContractException>(() => seerr.CompleteSetupAsync(Token)); Assert.Equal("seerr.initialize.prerequisites-missing", e.Code);
        Assert.False(await seerr.EnsureArrAsync(Connection(), Key, Token)); Assert.True(await seerr.CompleteSetupAsync(Token));
    }

    private static async Task Ready(SeerrContractClient seerr)
    {
        await Login(seerr); await seerr.EnsureLibrariesAsync(Libraries, Token);
        await seerr.EnsureArrAsync(Connection(), Key, Token); await seerr.EnsureArrAsync(Connection(ArrService.Sonarr), Key, Token);
    }
    private sealed class Fixture : HttpMessageHandler
    {
        private bool configured;
        private bool session;
        private bool lost;
        private bool initialized;
        public string Version { get; init; } = "3.5.0";
        public string? LoseRoute { get; init; }
        public bool ForeignType { get; init; }
        public bool WrongOwner { get; init; }
        public bool IgnoreEnables { get; init; }
        public bool CancelArrWrite { get; init; }
        public bool RejectTests { get; init; }
        public bool RejectJellyfinConnection { get; init; }
        public HttpStatusCode LoginStatus { get; init; } = HttpStatusCode.OK;
        public JsonArray LibraryList { get; } = [];
        public Dictionary<ArrService, JsonArray> Arr { get; } = new() { [ArrService.Radarr] = [], [ArrService.Sonarr] = [] };
        public int Logins { get; private set; }
        public int OwnerCreates { get; private set; }
        public int HostnameSubmissions { get; private set; }
        public int Syncs { get; private set; }
        public int Enables { get; private set; }
        public int Tests { get; private set; }
        public int ArrCreates { get; private set; }
        public int Initializes { get; private set; }
        public HttpClient Client() { session = false; return new(this, false) { BaseAddress = new("http://localhost:5055/") }; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                if (path == "/api/v1/status") return Reply(new JsonObject { ["version"] = Version });
                if (path == "/api/v1/settings/public") return Reply(new JsonObject { ["mediaServerType"] = ForeignType ? 1 : configured ? 2 : 4, ["initialized"] = initialized });
                if (path == "/api/v1/auth/me") return session ? Reply(new JsonObject { ["id"] = 1, ["permissions"] = 2, ["jellyfinUserId"] = WrongOwner ? "foreign" : User }) : new(HttpStatusCode.Unauthorized);
                if (!session) return new(HttpStatusCode.Unauthorized);
                if (path == "/api/v1/settings/jellyfin") return Reply(new JsonObject { ["serverId"] = Server, ["ip"] = "jellyfin", ["port"] = 8096, ["useSsl"] = false, ["urlBase"] = "", ["apiKey"] = "synthetic-sensitive-value" });
                if (path == "/api/v1/settings/jellyfin/users") return RejectJellyfinConnection ? new(HttpStatusCode.BadGateway) : Reply(new JsonArray(new JsonObject { ["id"] = User }));
                if (path == "/api/v1/settings/jellyfin/library") return Reply(LibraryList);
                return Reply(Arr[path.EndsWith("/radarr", StringComparison.Ordinal) ? ArrService.Radarr : ArrService.Sonarr]);
            }
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            var status = HttpStatusCode.OK;
            JsonNode response = new JsonObject();
            if (path == "/api/v1/auth/jellyfin")
            {
                Logins++;
                if (LoginStatus != HttpStatusCode.OK) return new(LoginStatus) { Content = new StringContent("synthetic-sensitive-value") };
                if (body!.ContainsKey("hostname")) { HostnameSubmissions++; Assert.False(configured); }
                else Assert.True(configured);
                if (!configured) OwnerCreates++;
                configured = true; session = true;
            }
            else if (path == "/api/v1/settings/jellyfin/library/sync")
            {
                Syncs++;
                foreach (var library in Libraries) LibraryList.Add(new JsonObject { ["id"] = library.Id, ["name"] = library.Name, ["type"] = library.CollectionType == "movies" ? "movie" : "show", ["enabled"] = false });
            }
            else if (path.StartsWith("/api/v1/settings/jellyfin/library/", StringComparison.Ordinal))
            { Enables++; if (!IgnoreEnables) LibraryList.Single(l => l!["id"]!.GetValue<string>() == path.Split('/')[^1])!["enabled"] = true; }
            else if (path == "/api/v1/settings/initialize") { initialized = true; Initializes++; }
            else
            {
                var service = path.Contains("/radarr", StringComparison.Ordinal) ? ArrService.Radarr : ArrService.Sonarr;
                if (path.EndsWith("/test", StringComparison.Ordinal))
                {
                    Tests++; if (RejectTests) return new(HttpStatusCode.InternalServerError) { Content = new StringContent("synthetic-sensitive-value") };
                    response = new JsonObject { ["profiles"] = new JsonArray(new JsonObject { ["id"] = 4, ["name"] = "HD-1080p" }), ["rootFolders"] = new JsonArray(new JsonObject { ["id"] = 1, ["path"] = service == ArrService.Radarr ? "/data/media/movies" : "/data/media/tv" }), ["urlBase"] = "" };
                }
                else
                {
                    ArrCreates++; body!["id"] = 0; Arr[service].Add(body); status = HttpStatusCode.Created;
                    if (CancelArrWrite) throw new OperationCanceledException(cancellationToken);
                }
            }
            if (path == LoseRoute && !lost) { lost = true; throw new HttpRequestException("synthetic-sensitive-value"); }
            return new(status) { Content = new StringContent(response.ToJsonString()) };
        }
        private static HttpResponseMessage Reply(JsonNode body) => new(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) };
    }
}
