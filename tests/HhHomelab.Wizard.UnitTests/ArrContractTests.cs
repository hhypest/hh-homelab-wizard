using System.Net;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class ArrContractTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static ArrDownloadConnection Connection => new("qbittorrent", 9080, "movies");

    [Theory]
    [InlineData(ArrService.Radarr, "6.4.4.10685", "v3")]
    [InlineData(ArrService.Sonarr, "4.0.20.3014", "v3")]
    [InlineData(ArrService.Prowlarr, "2.6.5.5623", "v1")]
    public async Task ExactVersionUsesApiKeyAndCorrectPrefix(
        ArrService service,
        string version,
        string prefix
    )
    {
        using var fixture = new Fixture { Version = version, Prefix = prefix };
        using var http = fixture.Client();
        Assert.Equal(
            version,
            await new ArrContractClient(http, service, Key).GetVersionAsync(Token)
        );
    }

    [Fact]
    public async Task WrongVersionBlocksMutation()
    {
        using var f = new Fixture { Version = "6.4.5.1" };
        using var h = f.Client();
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            new ArrContractClient(h, ArrService.Radarr, Key).EnsureRootFolderAsync(
                "/data/media/movies",
                Token
            )
        );
        Assert.Equal("arr.version.unsupported", error.Code);
        Assert.Equal(0, f.Writes);
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("/data/torrents/movies")]
    [InlineData("/data/media/../foreign")]
    [InlineData("/data/media//movies")]
    [InlineData("/data/media/films\\foreign")]
    public async Task UnsafeRootsNeverCallServer(string path)
    {
        using var f = new Fixture();
        using var h = f.Client();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new ArrContractClient(h, ArrService.Radarr, Key).EnsureRootFolderAsync(path, Token)
        );
        Assert.Equal(0, f.Requests);
    }

    [Fact]
    public async Task LostRootResponseIsObservedWithoutSecondPost()
    {
        using var f = new Fixture { LoseResponse = true };
        using var h = f.Client();
        var api = new ArrContractClient(h, ArrService.Radarr, Key);
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            api.EnsureRootFolderAsync("/data/media/movies", Token)
        );
        Assert.Equal("arr.root.result-unknown", error.Code);
        Assert.False(await api.EnsureRootFolderAsync("/data/media/movies", Token));
        Assert.Equal(1, f.Writes);
    }

    [Theory]
    [InlineData(false, "arr.root.inaccessible")]
    [InlineData(true, "arr.root.conflict")]
    public async Task ExistingUnsafeRootIsPreserved(bool foreign, string code)
    {
        using var f = new Fixture();
        f.Roots.Add(
            new JsonObject
            {
                ["id"] = 1,
                ["path"] = foreign ? "/data/media/foreign" : "/data/media/movies",
                ["accessible"] = foreign,
            }
        );
        using var h = f.Client();
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            new ArrContractClient(h, ArrService.Radarr, Key).EnsureRootFolderAsync(
                "/data/media/movies",
                Token
            )
        );
        Assert.Equal(code, error.Code);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task SuccessfulResponseRequiresObservedRoot()
    {
        using var f = new Fixture { IgnoreWrites = true };
        using var h = f.Client();
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            new ArrContractClient(h, ArrService.Radarr, Key).EnsureRootFolderAsync(
                "/data/media/movies",
                Token
            )
        );
        Assert.Equal("arr.root.not-applied", error.Code);
    }

    [Fact]
    public async Task MaskedProviderUsesItsIdAndTwoConnectionTestsOnRepeat()
    {
        using var f = new Fixture();
        using var h = f.Client();
        var api = new ArrContractClient(h, ArrService.Radarr, Key);
        Assert.True(
            await api.EnsureDownloadClientAsync(Connection, "wizard", "synthetic-password", Token)
        );
        Assert.False(
            await api.EnsureDownloadClientAsync(Connection, "wizard", "synthetic-password", Token)
        );
        Assert.Equal(1, f.Writes);
        Assert.Equal(4, f.Tests);
        Assert.Equal(
            "********",
            f.Providers[0]!["fields"]!
                .AsArray()
                .Single(n => n!["name"]!.GetValue<string>() == "password")![
                "value"
            ]!.GetValue<string>()
        );
    }

    [Fact]
    public async Task LostProviderResponseIsReconciledWithConnectionTest()
    {
        using var f = new Fixture { LoseResponse = true };
        using var h = f.Client();
        var api = new ArrContractClient(h, ArrService.Radarr, Key);
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            api.EnsureDownloadClientAsync(Connection, "wizard", "synthetic-password", Token)
        );
        Assert.Equal("arr.provider.result-unknown", error.Code);
        Assert.False(
            await api.EnsureDownloadClientAsync(Connection, "wizard", "synthetic-password", Token)
        );
        Assert.Equal(1, f.Writes);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("useSsl")]
    [InlineData("movieCategory")]
    public async Task ConflictingProviderIsNeverOverwritten(string field)
    {
        using var f = new Fixture();
        using var h = f.Client();
        var api = new ArrContractClient(h, ArrService.Radarr, Key);
        await api.EnsureDownloadClientAsync(Connection, "wizard", "synthetic-password", Token);
        var item = f.Providers[0]!["fields"]!
            .AsArray()
            .Single(n => n!["name"]!.GetValue<string>() == field)!;
        item["value"] = "foreign-value";
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            api.EnsureDownloadClientAsync(Connection, "wizard", "synthetic-password", Token)
        );
        Assert.Equal("arr.provider.conflict", error.Code);
        Assert.Equal(1, f.Writes);
    }

    [Fact]
    public async Task FailedTestOfSavedPasswordDoesNotClaimSuccessOrRewrite()
    {
        using var f = new Fixture();
        using var h = f.Client();
        var api = new ArrContractClient(h, ArrService.Radarr, Key);
        await api.EnsureDownloadClientAsync(Connection, "wizard", "synthetic-password", Token);
        f.RejectTests = true;
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            api.EnsureDownloadClientAsync(Connection, "wizard", "synthetic-password", Token)
        );
        Assert.Equal("arr.provider.test.http-error", error.Code);
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Equal(1, f.Writes);
    }

    [Fact]
    public async Task ProwlarrApplicationsPreserveIdAndSkipDuplicatePost()
    {
        using var f = new Fixture
        {
            Prefix = "v1",
            Version = "2.6.5.5623",
            Applications = true,
        };
        using var h = f.Client();
        var api = new ArrContractClient(h, ArrService.Prowlarr, Key);
        Assert.True(
            await api.EnsureApplicationAsync(
                ArrService.Radarr,
                new("http://prowlarr:9696/"),
                new("http://radarr:7878/"),
                Key,
                Token
            )
        );
        Assert.False(
            await api.EnsureApplicationAsync(
                ArrService.Radarr,
                new("http://prowlarr:9696/"),
                new("http://radarr:7878/"),
                Key,
                Token
            )
        );
        Assert.Equal(1, f.Writes);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task MutationErrorsExposeOnlyCodeAndStatus(int status)
    {
        using var f = new Fixture { MutationStatus = (HttpStatusCode)status };
        using var h = f.Client();
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            new ArrContractClient(h, ArrService.Radarr, Key).EnsureRootFolderAsync(
                "/data/media/movies",
                Token
            )
        );
        Assert.Equal("arr.root.http-error", error.Message);
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(1, f.Writes);
    }

    [Fact]
    public async Task CancellationDuringWriteRequiresObservationAndDoesNotRetry()
    {
        using var f = new Fixture { CancelWrite = true };
        using var h = f.Client();
        var api = new ArrContractClient(h, ArrService.Radarr, Key);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            api.EnsureRootFolderAsync("/data/media/movies", Token)
        );
        Assert.False(await api.EnsureRootFolderAsync("/data/media/movies", Token));
        Assert.Equal(1, f.Writes);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"version\":123}")]
    [InlineData("synthetic-sensitive-value")]
    public async Task MalformedResponsesAreSanitized(string body)
    {
        using var f = new Fixture { StatusJson = body };
        using var h = f.Client();
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            new ArrContractClient(h, ArrService.Radarr, Key).GetVersionAsync(Token)
        );
        Assert.Equal("arr.response-invalid", error.Message);
    }

    [Theory]
    [InlineData("none", "enabled", true)]
    [InlineData("forms", "enabled", false)]
    public async Task AuthenticationWriteOrRepeatAlwaysProvesFreshUiLogin(
        string method,
        string required,
        bool changed
    )
    {
        using var f = new Fixture { AuthMethod = method, AuthRequired = required };
        using var h = f.Client();
        using var uiHandler = new UiFixture();
        using var ui = uiHandler.Client();
        Assert.Equal(
            changed,
            await new ArrContractClient(h, ArrService.Radarr, Key).ConfigureAuthenticationAsync(
                "wizard",
                "synthetic-password",
                ui,
                Token
            )
        );
        Assert.Equal(changed ? 1 : 0, f.Writes);
        Assert.Equal(3, uiHandler.Requests);
    }

    [Theory]
    [InlineData("basic", "enabled")]
    [InlineData("forms", "disabledForLocalAddresses")]
    public async Task ForeignAuthenticationIsPreserved(string method, string required)
    {
        using var f = new Fixture { AuthMethod = method, AuthRequired = required };
        using var h = f.Client();
        using var uiHandler = new UiFixture();
        using var ui = uiHandler.Client();
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            new ArrContractClient(h, ArrService.Radarr, Key).ConfigureAuthenticationAsync(
                "wizard",
                "synthetic-password",
                ui,
                Token
            )
        );
        Assert.Equal("arr.authentication.conflict", error.Code);
        Assert.Equal(0, f.Writes);
        Assert.Equal(0, uiHandler.Requests);
    }

    [Theory]
    [InlineData(true, false, "arr.ui.context-preauthenticated-or-policy-invalid")]
    [InlineData(false, true, "arr.ui.authentication.rejected")]
    public async Task UiBypassAndWrongPasswordNeverCountAsSuccess(
        bool bypass,
        bool rejected,
        string code
    )
    {
        using var f = new Fixture { AuthMethod = "forms" };
        using var h = f.Client();
        using var uiHandler = new UiFixture { Bypass = bypass, Rejected = rejected };
        using var ui = uiHandler.Client();
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            new ArrContractClient(h, ArrService.Radarr, Key).ConfigureAuthenticationAsync(
                "wizard",
                "synthetic-password",
                ui,
                Token
            )
        );
        Assert.Equal(code, error.Code);
        Assert.Equal(0, f.Writes);
    }

    [Fact]
    public async Task LostAuthenticationResponseIsObservedAndCredentialsProvenWithoutSecondPut()
    {
        using var f = new Fixture { LoseResponse = true };
        using var h = f.Client();
        var api = new ArrContractClient(h, ArrService.Radarr, Key);
        using var firstHandler = new UiFixture();
        using var first = firstHandler.Client();
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            api.ConfigureAuthenticationAsync("wizard", "synthetic-password", first, Token)
        );
        Assert.Equal("arr.authentication.result-unknown", error.Code);
        Assert.Equal(0, firstHandler.Requests);
        using var nextHandler = new UiFixture();
        using var next = nextHandler.Client();
        Assert.False(
            await api.ConfigureAuthenticationAsync("wizard", "synthetic-password", next, Token)
        );
        Assert.Equal(1, f.Writes);
    }

    [Fact]
    public async Task AuthenticationSuccessRequiresObservedPolicy()
    {
        using var f = new Fixture { IgnoreWrites = true };
        using var h = f.Client();
        using var u = new UiFixture();
        using var ui = u.Client();
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            new ArrContractClient(h, ArrService.Radarr, Key).ConfigureAuthenticationAsync(
                "wizard",
                "synthetic-password",
                ui,
                Token
            )
        );
        Assert.Equal("arr.authentication.not-applied", error.Code);
        Assert.Equal(0, u.Requests);
    }

    [Fact]
    public async Task ForeignProviderNameAndDuplicatesCannotBeTakenOver()
    {
        using var f = new Fixture();
        using var h = f.Client();
        var api = new ArrContractClient(h, ArrService.Radarr, Key);
        await api.EnsureDownloadClientAsync(Connection, "wizard", "synthetic-password", Token);
        f.Providers.Add(f.Providers[0]!.DeepClone());
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            api.EnsureDownloadClientAsync(Connection, "wizard", "synthetic-password", Token)
        );
        Assert.Equal("arr.provider.conflict", error.Code);
        f.Providers.RemoveAt(1);
        f.Providers[0]!["name"] = "foreign";
        error = await Assert.ThrowsAsync<ArrContractException>(() =>
            api.EnsureDownloadClientAsync(Connection, "wizard", "synthetic-password", Token)
        );
        Assert.Equal("arr.provider.conflict", error.Code);
        Assert.Equal(1, f.Writes);
    }

    [Theory]
    [InlineData("http://user:password@localhost/")]
    [InlineData("http://localhost/?key=secret")]
    [InlineData("http://localhost/#secret")]
    [InlineData("http://localhost/api")]
    public void UnsafeBaseUrlIsRejected(string endpoint)
    {
        using var f = new Fixture();
        using var h = f.Client();
        h.BaseAddress = new(endpoint);
        Assert.Throws<ArgumentException>(() => new ArrContractClient(h, ArrService.Radarr, Key));
        Assert.Equal(0, f.Requests);
    }

    [Theory]
    [InlineData("qbittorrent;exec", 9080, "movies")]
    [InlineData("qbittorrent", 0, "movies")]
    [InlineData("qbittorrent", 9080, "tv")]
    public async Task InvalidDownloadParametersNeverCallServer(
        string host,
        int port,
        string category
    )
    {
        using var f = new Fixture();
        using var h = f.Client();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new ArrContractClient(h, ArrService.Radarr, Key).EnsureDownloadClientAsync(
                new(host, port, category),
                "wizard",
                "synthetic-password",
                Token
            )
        );
        Assert.Equal(0, f.Requests);
    }

    [Fact]
    public async Task ForeignLoginRedirectIsRejectedWithoutPostingCredentials()
    {
        using var f = new Fixture { AuthMethod = "forms" };
        using var h = f.Client();
        using var u = new UiFixture { ForeignRedirect = true };
        using var ui = u.Client();
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            new ArrContractClient(h, ArrService.Radarr, Key).ConfigureAuthenticationAsync(
                "wizard",
                "synthetic-password",
                ui,
                Token
            )
        );
        Assert.Equal("arr.ui.context-preauthenticated-or-policy-invalid", error.Code);
        Assert.Equal(1, u.Requests);
    }

    private sealed class UiFixture : HttpMessageHandler
    {
        public bool Bypass { get; init; }
        public bool Rejected { get; init; }
        public bool ForeignRedirect { get; init; }
        public int Requests { get; private set; }

        public HttpClient Client() =>
            new(this, false) { BaseAddress = new("http://localhost:7878/") };

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests++;
            Assert.False(request.Headers.Contains("X-Api-Key"));
            var status =
                Requests == 3 || Requests == 1 && Bypass ? HttpStatusCode.OK : HttpStatusCode.Found;
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.Found)
                response.Headers.Location = ForeignRedirect
                    ? new("https://foreign.example/login")
                    : new(Requests == 1 || Rejected ? "/login" : "/", UriKind.Relative);
            return Task.FromResult(response);
        }
    }

    private sealed class Fixture : HttpMessageHandler
    {
        public string Version { get; init; } = "6.4.4.10685";
        public string Prefix { get; init; } = "v3";
        public string? StatusJson { get; init; }
        public bool Applications { get; init; }
        public bool LoseResponse { get; init; }
        public bool CancelWrite { get; init; }
        public bool IgnoreWrites { get; init; }
        public bool RejectTests { get; set; }
        public string AuthMethod { get; set; } = "none";
        public string AuthRequired { get; set; } = "enabled";
        public HttpStatusCode MutationStatus { get; init; } = HttpStatusCode.OK;
        public JsonArray Roots { get; } = [];
        public JsonArray Providers { get; } = [];
        public int Requests { get; private set; }
        public int Writes { get; private set; }
        public int Tests { get; private set; }

        public HttpClient Client() =>
            new(this, false) { BaseAddress = new("http://localhost:7878/") };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests++;
            Assert.Equal(Key, Assert.Single(request.Headers.GetValues("X-Api-Key")));
            var prefix = "/api/" + Prefix + "/";
            Assert.StartsWith(prefix, request.RequestUri!.AbsolutePath);
            var route = request.RequestUri.AbsolutePath[prefix.Length..];
            if (request.Method == HttpMethod.Get)
            {
                if (route == "system/status")
                    return Reply(
                        StatusJson ?? new JsonObject { ["version"] = Version }.ToJsonString()
                    );
                if (route == "rootfolder")
                    return Reply(Roots.ToJsonString());
                if (route == "config/host")
                    return Reply(
                        new JsonObject
                        {
                            ["id"] = 1,
                            ["username"] = "wizard",
                            ["authenticationMethod"] = AuthMethod,
                            ["authenticationRequired"] = AuthRequired,
                        }.ToJsonString()
                    );
                if (route.EndsWith("/schema", StringComparison.Ordinal))
                    return Reply(new JsonArray(Schema()).ToJsonString());
                return Reply(Providers.ToJsonString());
            }
            var payload = JsonNode
                .Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!
                .AsObject();
            if (route.EndsWith("/test", StringComparison.Ordinal))
            {
                Tests++;
                if (Providers.Count > 0)
                    Assert.Equal(1, payload["id"]!.GetValue<int>());
                return new(RejectTests ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
                {
                    Content = new StringContent("synthetic-sensitive-value"),
                };
            }
            Writes++;
            if (MutationStatus != HttpStatusCode.OK)
                return new(MutationStatus)
                {
                    Content = new StringContent("synthetic-sensitive-value"),
                };
            if (!IgnoreWrites)
            {
                if (route.StartsWith("config/host/", StringComparison.Ordinal))
                {
                    AuthMethod = payload["authenticationMethod"]!.GetValue<string>();
                    AuthRequired = payload["authenticationRequired"]!.GetValue<string>();
                }
                else
                {
                    payload["id"] = 1;
                    if (route == "rootfolder")
                    {
                        payload["accessible"] = true;
                        Roots.Add(payload);
                    }
                    else
                    {
                        foreach (var field in payload["fields"]!.AsArray())
                            if (
                                field!["name"]!.GetValue<string>()
                                == (Applications ? "apiKey" : "password")
                            )
                                field["value"] = "********";
                        Providers.Add(payload);
                    }
                }
            }
            if (CancelWrite)
                throw new OperationCanceledException(cancellationToken);
            if (LoseResponse)
                throw new HttpRequestException("synthetic-sensitive-value");
            return new(HttpStatusCode.Created);
        }

        private JsonObject Schema()
        {
            var names = Applications
                ? new[] { "prowlarrUrl", "baseUrl", "apiKey" }
                :
                [
                    "host",
                    "port",
                    "useSsl",
                    "urlBase",
                    "username",
                    "password",
                    "apiKey",
                    "movieCategory",
                ];
            return new JsonObject
            {
                ["implementation"] = Applications ? "Radarr" : "QBittorrent",
                ["configContract"] = Applications ? "RadarrSettings" : "QBittorrentSettings",
                ["fields"] = new JsonArray(
                    names.Select(n => (JsonNode)new JsonObject { ["name"] = n }).ToArray()
                ),
            };
        }

        private static HttpResponseMessage Reply(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
