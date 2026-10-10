using System.Net;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class JellyfinContractTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static MediaLibrary Movies => new("Фильмы", "movies", "/data/media/movies");

    [Theory]
    [InlineData("/etc")]
    [InlineData("/data/torrents/movies")]
    [InlineData("/data/media/../foreign")]
    [InlineData("/data/media//movies")]
    public async Task UnsafeLibraryNeverCallsServer(string path)
    {
        using var f = new Fixture(); using var h = f.Client();
        await Assert.ThrowsAsync<ArgumentException>(() => new JellyfinContractClient(h).EnsureLibraryAsync(Movies with { Path = path }, Token)); Assert.Equal(0, f.Requests);
    }

    [Fact]
    public async Task LibraryWriteRequiresVerifiedAdministrator()
    {
        using var f = new Fixture(); using var h = f.Client();
        var e = await Assert.ThrowsAsync<MediaContractException>(() => new JellyfinContractClient(h).EnsureLibraryAsync(Movies, Token));
        Assert.Equal("jellyfin.session.required", e.Code); Assert.Equal(0, f.Requests);
    }

    [Theory]
    [InlineData("/Startup/User")]
    [InlineData("/Startup/Complete")]
    public async Task LostStartupResponseIsObservedBeforeAnotherPasswordOrCompleteWrite(string route)
    {
        using var f = new Fixture { LoseRoute = route }; using var h = f.Client(); var jf = new JellyfinContractClient(h);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => jf.BootstrapAsync("wizard", "synthetic-password", Token));
        Assert.EndsWith("result-unknown", e.Code);
        await jf.BootstrapAsync("wizard", "synthetic-password", Token);
        Assert.False(await jf.BootstrapAsync("wizard", "synthetic-password", Token));
        Assert.Equal(1, f.PasswordWrites); Assert.Equal(1, f.CompleteWrites); Assert.True(f.Completed);
    }

    [Fact]
    public async Task CancellationAfterPasswordWriteDoesNotCauseAnotherPasswordWrite()
    {
        using var f = new Fixture { CancelPasswordWrite = true }; using var h = f.Client(); var jf = new JellyfinContractClient(h);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jf.BootstrapAsync("wizard", "synthetic-password", Token));
        Assert.True(await jf.BootstrapAsync("wizard", "synthetic-password", Token)); Assert.Equal(1, f.PasswordWrites);
    }

    [Fact]
    public async Task LostLibraryResponseIsReconciledWithoutDuplicateAndRepeatSkipsWrite()
    {
        using var f = new Fixture { LoseRoute = "/Library/VirtualFolders" }; using var h = f.Client(); var jf = new JellyfinContractClient(h);
        await jf.BootstrapAsync("wizard", "synthetic-password", Token);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => jf.EnsureLibraryAsync(Movies, Token)); Assert.Equal("jellyfin.library.result-unknown", e.Code);
        Assert.False(await jf.EnsureLibraryAsync(Movies, Token)); Assert.Equal(1, f.LibraryWrites); Assert.Single(await jf.GetLibrariesAsync(Token));
    }

    [Theory]
    [InlineData("Path")]
    [InlineData("CollectionType")]
    [InlineData("Language")]
    public async Task ExistingLibraryConflictIsPreserved(string changed)
    {
        using var f = new Fixture(); using var h = f.Client(); var jf = new JellyfinContractClient(h); await jf.BootstrapAsync("wizard", "synthetic-password", Token);
        await jf.EnsureLibraryAsync(Movies, Token);
        if (changed == "Path") f.Libraries[0]!["Locations"] = new JsonArray("/data/media/foreign");
        else if (changed == "Language") f.Libraries[0]!["LibraryOptions"]!["PreferredMetadataLanguage"] = "en";
        else f.Libraries[0]!["CollectionType"] = "tvshows";
        var e = await Assert.ThrowsAsync<MediaContractException>(() => jf.EnsureLibraryAsync(Movies, Token)); Assert.Equal("jellyfin.library.conflict", e.Code); Assert.Equal(1, f.LibraryWrites);
    }

    [Fact]
    public async Task HttpSuccessWithoutLibraryIsNotSuccess()
    {
        using var f = new Fixture { IgnoreLibraryWrites = true }; using var h = f.Client(); var jf = new JellyfinContractClient(h); await jf.BootstrapAsync("wizard", "synthetic-password", Token);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => jf.EnsureLibraryAsync(Movies, Token)); Assert.Equal("jellyfin.library.not-applied", e.Code);
    }

    [Fact]
    public async Task ForeignStartupPasswordCannotBeReset()
    {
        using var f = new Fixture { PasswordSet = true, Username = "foreign" }; using var h = f.Client();
        var e = await Assert.ThrowsAsync<MediaContractException>(() => new JellyfinContractClient(h).BootstrapAsync("wizard", "synthetic-password", Token));
        Assert.Equal("jellyfin.read.http-error", e.Code); Assert.Equal(HttpStatusCode.Unauthorized, e.StatusCode); Assert.Equal(0, f.PasswordWrites);
    }

    [Fact]
    public async Task FailedLoginRetiresPriorSession()
    {
        using var f = new Fixture(); using var h = f.Client(); var jf = new JellyfinContractClient(h); await jf.BootstrapAsync("wizard", "synthetic-password", Token);
        await Assert.ThrowsAsync<MediaContractException>(() => jf.LoginAdministratorAsync("wizard", "wrong-password", Token));
        var e = await Assert.ThrowsAsync<MediaContractException>(() => jf.EnsureLibraryAsync(Movies, Token)); Assert.Equal("jellyfin.session.required", e.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonAdministratorAndUnexpectedIdentityAreRejected(bool wrongServer)
    {
        using var f = new Fixture { PasswordSet = true, Username = "wizard", NonAdmin = !wrongServer, WrongServer = wrongServer }; using var h = f.Client();
        var e = await Assert.ThrowsAsync<MediaContractException>(() => new JellyfinContractClient(h).LoginAdministratorAsync("wizard", "synthetic-password", Token)); Assert.Equal("jellyfin.login.identity-mismatch", e.Code);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("synthetic-sensitive-value")]
    public async Task InvalidServerResponseCannotLeakBody(string body)
    {
        using var f = new Fixture { PublicJson = body }; using var h = f.Client();
        var e = await Assert.ThrowsAsync<MediaContractException>(() => new JellyfinContractClient(h).GetStartupCompletedAsync(Token));
        Assert.DoesNotContain("synthetic-sensitive-value", e.ToString());
    }

    [Fact]
    public async Task VersionMismatchBlocksBootstrap()
    {
        using var f = new Fixture { Version = "12.3.0" }; using var h = f.Client();
        var e = await Assert.ThrowsAsync<MediaContractException>(() => new JellyfinContractClient(h).BootstrapAsync("wizard", "synthetic-password", Token)); Assert.Equal("jellyfin.version.unsupported", e.Code); Assert.Equal(0, f.PasswordWrites);
    }

    [Fact]
    public async Task FailureAfterSuccessfulLoginRetiresUnconfirmedToken()
    {
        using var f = new Fixture { Username = "wizard", PasswordSet = true, RejectMe = true }; using var h = f.Client(); var jf = new JellyfinContractClient(h);
        await Assert.ThrowsAsync<MediaContractException>(() => jf.LoginAdministratorAsync("wizard", "synthetic-password", Token));
        var e = await Assert.ThrowsAsync<MediaContractException>(() => jf.EnsureLibraryAsync(Movies, Token)); Assert.Equal("jellyfin.session.required", e.Code); Assert.Null(jf.AdministratorId);
    }

    private sealed class Fixture : HttpMessageHandler
    {
        private const string Server = "00000000000000000000000000000001";
        private const string User = "00000000000000000000000000000002";
        private bool lost;
        public string Version { get; init; } = "12.2.0";
        public string? PublicJson { get; init; }
        public string? LoseRoute { get; init; }
        public bool CancelPasswordWrite { get; init; }
        public bool IgnoreLibraryWrites { get; init; }
        public bool PasswordSet { get; set; }
        public string Username { get; set; } = "initial";
        public bool Completed { get; private set; }
        public bool NonAdmin { get; init; }
        public bool WrongServer { get; init; }
        public bool RejectMe { get; set; }
        private bool remote = true;
        private JsonObject config = new() { ["ServerName"] = "initial", ["UICulture"] = "en-US", ["MetadataCountryCode"] = "US", ["PreferredMetadataLanguage"] = "en" };
        public JsonArray Libraries { get; } = [];
        public int Requests { get; private set; }
        public int PasswordWrites { get; private set; }
        public int CompleteWrites { get; private set; }
        public int LibraryWrites { get; private set; }
        public HttpClient Client() => new(this, false) { BaseAddress = new("http://localhost:8096/") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var route = request.RequestUri!.AbsolutePath;
            Assert.False(request.Headers.Contains("X-Emby-Token"));
            var authorized = request.Headers.GetValues("Authorization").Single().Contains(", Token=", StringComparison.Ordinal);
            if (request.Method == HttpMethod.Get)
            {
                if (route == "/System/Info/Public") return Reply(PublicJson ?? new JsonObject { ["Version"] = Version, ["Id"] = Server, ["StartupWizardCompleted"] = Completed }.ToJsonString());
                if (route == "/Startup/User") return !PasswordSet || authorized ? Reply(new JsonObject { ["Name"] = Username }.ToJsonString()) : new(HttpStatusCode.Unauthorized);
                if (!authorized) return new(HttpStatusCode.Unauthorized);
                if (route == "/Users/Me") return RejectMe ? new(HttpStatusCode.Unauthorized) : Reply(UserJson().ToJsonString());
                if (route == "/System/Configuration/network") return Reply(new JsonObject { ["EnableRemoteAccess"] = remote }.ToJsonString());
                if (route == "/Startup/Configuration" || route == "/System/Configuration") return Reply(config.ToJsonString());
                Assert.Equal("/Library/VirtualFolders", route); return Reply(Libraries.ToJsonString());
            }
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            if (route == "/Users/AuthenticateByName")
            {
                if (!PasswordSet || body!["Username"]!.GetValue<string>() != Username || body["Pw"]!.GetValue<string>() != "synthetic-password") return new(HttpStatusCode.Unauthorized);
                return Reply(new JsonObject { ["AccessToken"] = "11111111111111111111111111111111", ["User"] = UserJson(), ["ServerId"] = WrongServer ? "foreign" : Server }.ToJsonString());
            }
            if (route == "/Startup/User")
            {
                if (PasswordSet) return new(HttpStatusCode.Forbidden);
                PasswordWrites++; PasswordSet = true; Username = body!["Name"]!.GetValue<string>();
                if (CancelPasswordWrite) throw new OperationCanceledException(cancellationToken);
            }
            else if (route == "/Startup/Configuration") config = body!;
            else if (route == "/Startup/RemoteAccess") remote = body!["EnableRemoteAccess"]!.GetValue<bool>();
            else if (route == "/Startup/Complete") { Completed = true; CompleteWrites++; }
            else
            {
                Assert.Equal("/Library/VirtualFolders", route); LibraryWrites++;
                if (!IgnoreLibraryWrites)
                {
                    var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
                    Assert.Equal("false", query["refreshLibrary"]);
                    Libraries.Add(new JsonObject { ["Name"] = query["name"], ["CollectionType"] = query["collectionType"], ["ItemId"] = "00000000000000000000000000000003",
                        ["Locations"] = new JsonArray(body!["LibraryOptions"]!["PathInfos"]![0]!["Path"]!.DeepClone()), ["LibraryOptions"] = body["LibraryOptions"]!.DeepClone() });
                }
            }
            if (route == LoseRoute && !lost) { lost = true; throw new HttpRequestException("synthetic-sensitive-value"); }
            return new(HttpStatusCode.NoContent);
        }
        private JsonObject UserJson() => new() { ["Id"] = User, ["Name"] = Username, ["Policy"] = new JsonObject { ["IsAdministrator"] = !NonAdmin } };
        private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json) };
    }
}
