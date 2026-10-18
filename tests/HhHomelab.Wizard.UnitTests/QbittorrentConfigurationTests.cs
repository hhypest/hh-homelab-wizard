using System.Net;
using System.Text.Json;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class QbittorrentConfigurationTests
{
    private static QbittorrentDownloadSettings Desired =>
        new("/data/torrents", "/data/torrents/incomplete", true);

    [Theory]
    [InlineData("/etc")]
    [InlineData("/dataevil/torrents")]
    [InlineData("/data/../outside")]
    [InlineData("/data//torrents")]
    [InlineData("/data/./torrents")]
    [InlineData("/data/foo\\bar")]
    [InlineData("/data/foo\nbar")]
    public async Task InvalidPathsNeverSendRequests(string path)
    {
        using var fixture = new ApiFixture();
        using var http = fixture.CreateClient();
        var api = new QbittorrentContractClient(http);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            api.ConfigureDownloadsAsync(
                Desired with
                {
                    SavePath = path,
                },
                TestContext.Current.CancellationToken
            )
        );
        await Assert.ThrowsAsync<ArgumentException>(() =>
            api.EnsureCategoryAsync("movies", path, TestContext.Current.CancellationToken)
        );
        Assert.Equal(0, fixture.Requests);
    }

    [Fact]
    public async Task WritesRequireThisClientsVerifiedSession()
    {
        using var fixture = new ApiFixture();
        using var http = fixture.CreateClient();
        var api = new QbittorrentContractClient(http);
        var exception = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
            api.ConfigureDownloadsAsync(Desired, TestContext.Current.CancellationToken)
        );
        Assert.Equal("qb.session.required", exception.Code);
        Assert.Equal(0, fixture.Requests);
    }

    [Fact]
    public async Task DownloadSettingsAreVerifiedAndRepeatedWriteIsSkipped()
    {
        using var fixture = new ApiFixture();
        using var http = fixture.CreateClient();
        var api = await LoginAsync(http);
        Assert.True(
            await api.ConfigureDownloadsAsync(Desired, TestContext.Current.CancellationToken)
        );
        Assert.False(
            await api.ConfigureDownloadsAsync(
                Desired with
                {
                    SavePath = "/data/torrents/",
                },
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(1, fixture.PreferenceWrites);
        Assert.True(
            Desired.Matches(
                await api.GetDownloadSettingsAsync(TestContext.Current.CancellationToken)
            )
        );
    }

    [Fact]
    public async Task SuccessfulHttpResponseWithoutAppliedSettingsIsNotSuccess()
    {
        using var fixture = new ApiFixture { IgnoreWrites = true };
        using var http = fixture.CreateClient();
        var api = await LoginAsync(http);
        var exception = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
            api.ConfigureDownloadsAsync(Desired, TestContext.Current.CancellationToken)
        );
        Assert.Equal("qb.preferences.not-applied", exception.Code);
        Assert.Equal(1, fixture.PreferenceWrites);
    }

    [Fact]
    public async Task CategoryResponseLossRequiresObservationBeforeRepeat()
    {
        using var fixture = new ApiFixture { LoseCategoryResponse = true };
        using var http = fixture.CreateClient();
        var api = await LoginAsync(http);
        var exception = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
            api.EnsureCategoryAsync(
                "movies",
                "/data/torrents/movies",
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal("qb.category.result-unknown", exception.Code);
        Assert.False(
            await api.EnsureCategoryAsync(
                "movies",
                "/data/torrents/movies",
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal(1, fixture.CategoryWrites);
        Assert.Equal("/data/torrents/movies", Assert.Single(fixture.Categories).Value);
    }

    [Fact]
    public async Task ConflictingCategoryIsPreserved()
    {
        using var fixture = new ApiFixture();
        fixture.Categories["movies"] = "/data/foreign";
        using var http = fixture.CreateClient();
        var api = await LoginAsync(http);
        var exception = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
            api.EnsureCategoryAsync(
                "movies",
                "/data/torrents/movies",
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal("qb.category.conflict", exception.Code);
        Assert.Equal(0, fixture.CategoryWrites);
        Assert.Equal("/data/foreign", fixture.Categories["movies"]);
    }

    [Fact]
    public async Task CategorySuccessRequiresObservedState()
    {
        using var fixture = new ApiFixture { IgnoreWrites = true };
        using var http = fixture.CreateClient();
        var api = await LoginAsync(http);
        var exception = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
            api.EnsureCategoryAsync(
                "tv",
                "/data/torrents/tv",
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal("qb.category.not-applied", exception.Code);
        Assert.Equal(1, fixture.CategoryWrites);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(409)]
    [InlineData(500)]
    public async Task CategoryErrorsPreserveStatusWithoutBody(int status)
    {
        using var fixture = new ApiFixture { MutationStatus = (HttpStatusCode)status };
        using var http = fixture.CreateClient();
        var api = await LoginAsync(http);
        var exception = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
            api.EnsureCategoryAsync(
                "tv",
                "/data/torrents/tv",
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal("qb.category.http-error", exception.Message);
        Assert.Equal((HttpStatusCode)status, exception.StatusCode);
        Assert.Equal(1, fixture.CategoryWrites);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"save_path\":null,\"temp_path\":\"/data/tmp\",\"temp_path_enabled\":true}")]
    [InlineData("{\"save_path\":123,\"temp_path\":\"/data/tmp\",\"temp_path_enabled\":true}")]
    [InlineData("synthetic-sensitive-value")]
    public async Task MalformedPreferencesAreSanitized(string json)
    {
        using var fixture = new ApiFixture { PreferencesJson = json };
        using var http = fixture.CreateClient();
        var api = await LoginAsync(http);
        var exception = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
            api.GetDownloadSettingsAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal("qb.preferences.response-invalid", exception.Message);
    }

    [Theory]
    [InlineData("ab", "synthetic-password")]
    [InlineData("user:name", "synthetic-password")]
    [InlineData("username", "short")]
    [InlineData("username", "password\nline")]
    public async Task InvalidCredentialsCannotPartiallyChangeUsername(
        string username,
        string password
    )
    {
        using var fixture = new ApiFixture();
        using var http = fixture.CreateClient();
        var api = new QbittorrentContractClient(http);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            api.ChangeCredentialsAsync(username, password, TestContext.Current.CancellationToken)
        );
        Assert.Equal(0, fixture.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CredentialWriteRetiresSessionEvenIfResponseIsLost(bool loseResponse)
    {
        using var fixture = new ApiFixture { LoseCredentialResponse = loseResponse };
        using var http = fixture.CreateClient();
        var api = await LoginAsync(http);
        if (loseResponse)
        {
            var exception = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
                api.ChangeCredentialsAsync(
                    "synthetic-user",
                    "synthetic-password",
                    TestContext.Current.CancellationToken
                )
            );
            Assert.Equal("qb.preferences.result-unknown", exception.Code);
        }
        else
            await api.ChangeCredentialsAsync(
                "synthetic-user",
                "synthetic-password",
                TestContext.Current.CancellationToken
            );
        var retired = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
            api.GetCategoriesAsync(TestContext.Current.CancellationToken)
        );
        Assert.Equal("qb.session.required", retired.Code);
        Assert.Equal(1, fixture.PreferenceWrites);
    }

    [Fact]
    public async Task CancellationDuringWriteHasUnknownResultAndDoesNotRetry()
    {
        using var fixture = new ApiFixture { CancelCategoryWrite = true };
        using var http = fixture.CreateClient();
        var api = await LoginAsync(http);
        var error = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
            api.EnsureCategoryAsync(
                "tv",
                "/data/torrents/tv",
                TestContext.Current.CancellationToken
            )
        );
        Assert.Equal("qb.category.result-unknown", error.Code);
        Assert.Equal(ContractFailureKind.Unknown, error.Kind);
        Assert.Equal(1, fixture.CategoryWrites);
    }

    private static async Task<QbittorrentContractClient> LoginAsync(HttpClient http)
    {
        var api = new QbittorrentContractClient(http);
        await api.LoginAsync(
            "synthetic-user",
            "synthetic-password",
            TestContext.Current.CancellationToken
        );
        return api;
    }

    private sealed class ApiFixture : HttpMessageHandler
    {
        private bool loggedIn;
        private QbittorrentDownloadSettings settings = new(
            "/downloads/",
            "/downloads/incomplete/",
            false
        );
        public Dictionary<string, string> Categories { get; } = new(StringComparer.Ordinal);
        public int Requests { get; private set; }
        public int PreferenceWrites { get; private set; }
        public int CategoryWrites { get; private set; }
        public bool IgnoreWrites { get; init; }
        public bool LoseCategoryResponse { get; init; }
        public bool LoseCredentialResponse { get; init; }
        public bool CancelCategoryWrite { get; init; }
        public HttpStatusCode MutationStatus { get; init; } = HttpStatusCode.OK;
        public string? PreferencesJson { get; init; }

        public HttpClient CreateClient() =>
            new(this, disposeHandler: false) { BaseAddress = new("http://localhost:9080/") };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests++;
            Assert.Equal("http://localhost:9080/", request.Headers.Referrer?.AbsoluteUri);
            var route = request.RequestUri!.AbsolutePath;
            if (route == "/api/v2/app/version")
                return loggedIn ? Reply("v5.2.4") : new(HttpStatusCode.Forbidden);
            if (route == "/api/v2/auth/login")
            {
                loggedIn = true;
                return new(HttpStatusCode.NoContent);
            }
            if (route == "/api/v2/app/preferences")
                return Reply(
                    PreferencesJson
                        ?? JsonSerializer.Serialize(
                            new
                            {
                                save_path = settings.SavePath,
                                temp_path = settings.TemporaryPath,
                                temp_path_enabled = settings.TemporaryPathEnabled,
                                proxy_password = "synthetic-sensitive-value",
                            }
                        )
                );
            if (route == "/api/v2/torrents/categories")
                return Reply(
                    JsonSerializer.Serialize(
                        Categories.ToDictionary(
                            p => p.Key,
                            p => new { name = p.Key, savePath = p.Value }
                        )
                    )
                );
            var fields = (await request.Content!.ReadAsStringAsync(cancellationToken))
                .Split('&')
                .Select(p => p.Split('=', 2))
                .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
            Assert.Equal(HttpMethod.Post, request.Method);
            if (route == "/api/v2/app/setPreferences")
            {
                PreferenceWrites++;
                using var document = JsonDocument.Parse(fields["json"]);
                var root = document.RootElement;
                if (root.TryGetProperty("web_ui_password", out var password))
                {
                    Assert.Equal("synthetic-password", password.GetString());
                    Assert.Equal("synthetic-user", root.GetProperty("web_ui_username").GetString());
                    if (LoseCredentialResponse)
                        throw new HttpRequestException("synthetic-sensitive-value");
                }
                else if (!IgnoreWrites)
                    settings = new(
                        root.GetProperty("save_path").GetString()!,
                        root.GetProperty("temp_path").GetString()!,
                        root.GetProperty("temp_path_enabled").GetBoolean()
                    );
                return new(HttpStatusCode.OK);
            }
            Assert.Equal("/api/v2/torrents/createCategory", route);
            CategoryWrites++;
            if (CancelCategoryWrite)
                throw new OperationCanceledException(cancellationToken);
            if (MutationStatus != HttpStatusCode.OK)
                return new(MutationStatus)
                {
                    Content = new StringContent("synthetic-sensitive-value"),
                };
            if (!IgnoreWrites)
                Categories[fields["category"]] = fields["savePath"];
            if (LoseCategoryResponse)
                throw new HttpRequestException("synthetic-sensitive-value");
            return new(HttpStatusCode.OK);
        }

        private static HttpResponseMessage Reply(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }
}
