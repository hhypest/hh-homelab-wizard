using System.Net;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class ConfigurationOperationAdapterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("radarr")]
    public async Task SampleBindingUsesObserveApplyVerifyAndSkipsConfirmedRepeat(string service)
    {
        using var fixture = new ApiFixture(service);
        await fixture.LoginAsync();
        var owner = new OperationFixture();
        var operation = fixture.Operation(OperationFixture.NewRequest(service));
        var scenario = new ConfigurationOperationScenario(owner.Journal, owner);
        Assert.Equal(OperationOutcomeKind.Applied, (await scenario.ExecuteAsync(operation, Token)).Kind);
        Assert.Equal(OperationOutcomeKind.AlreadySatisfied, (await scenario.ExecuteAsync(operation, Token)).Kind);
        Assert.Equal(1, fixture.Writes);
        Assert.True(fixture.ResourceReads >= 4);
        Assert.NotNull(owner.Journal.Entry!.Confirmation);
    }

    [Theory]
    [InlineData("qbittorrent", true)]
    [InlineData("qbittorrent", false)]
    [InlineData("radarr", true)]
    [InlineData("radarr", false)]
    public async Task DroppedWriteResponseRequiresObservationAndNeverBlindRepeat(string service, bool serverApplied)
    {
        using var fixture = new ApiFixture(service) { LoseResponse = true, ApplyOnServer = serverApplied };
        await fixture.LoginAsync();
        var owner = new OperationFixture();
        var request = OperationFixture.NewRequest(service);
        var scenario = new ConfigurationOperationScenario(owner.Journal, owner);
        Assert.Equal(OperationOutcomeKind.Unknown, (await scenario.ExecuteAsync(fixture.Operation(request), Token)).Kind);
        // A new adapter instance has no durable intent: the shared journal controls reconciliation.
        fixture.LoseResponse = false;
        Assert.Equal(serverApplied ? OperationOutcomeKind.Applied : OperationOutcomeKind.Unknown,
            (await new ConfigurationOperationScenario(owner.Journal, owner).ExecuteAsync(fixture.Operation(request), Token)).Kind);
        Assert.Equal(1, fixture.Writes);
    }

    [Theory]
    [InlineData("qbittorrent", HttpStatusCode.BadRequest)]
    [InlineData("qbittorrent", HttpStatusCode.Unauthorized)]
    [InlineData("qbittorrent", HttpStatusCode.Conflict)]
    [InlineData("radarr", HttpStatusCode.Forbidden)]
    [InlineData("radarr", HttpStatusCode.InternalServerError)]
    public async Task AmbiguousWriteStatusIsNotAProvenRejection(string service, HttpStatusCode status)
    {
        using var fixture = new ApiFixture(service) { WriteStatus = status };
        await fixture.LoginAsync();
        var owner = new OperationFixture();
        var result = await new ConfigurationOperationScenario(owner.Journal, owner).ExecuteAsync(fixture.Operation(OperationFixture.NewRequest(service)), Token);
        Assert.Equal(OperationOutcomeKind.Unknown, result.Kind);
        Assert.Equal(OperationErrorCode.WriteUncertain, result.Code);
        Assert.Equal(1, fixture.Writes);
        Assert.Null(owner.Journal.Entry!.Confirmation);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("radarr")]
    public async Task CallerCancellationAfterSendRetainsIntentAndUncertainty(string service)
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new ApiFixture(service) { CancelOnWrite = cancellation };
        await fixture.LoginAsync();
        var owner = new OperationFixture();
        var result = await new ConfigurationOperationScenario(owner.Journal, owner).ExecuteAsync(fixture.Operation(OperationFixture.NewRequest(service)), cancellation.Token);
        Assert.Equal(OperationOutcomeKind.Unknown, result.Kind);
        Assert.NotNull(owner.Journal.Entry);
        Assert.Null(owner.Journal.Entry.Confirmation);
        Assert.Equal(1, fixture.Writes);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("radarr")]
    public async Task CancellationDuringApplyBaselineRereadAfterIntentIsUnknown(string service)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        using var fixture = new ApiFixture(service) { CancelOnSecondResourceRead = cancellation };
        await fixture.LoginAsync();
        var owner = new OperationFixture();
        var request = OperationFixture.NewRequest(service);
        var result = await new ConfigurationOperationScenario(owner.Journal, owner).ExecuteAsync(fixture.Operation(request), cancellation.Token);
        Assert.Equal(OperationOutcomeKind.Unknown, result.Kind);
        Assert.Equal(0, fixture.Writes);
        Assert.NotNull(owner.Journal.Entry);
        Assert.Null(owner.Journal.Entry.Confirmation);
        Assert.Equal(OperationOutcomeKind.Unknown,
            (await new ConfigurationOperationScenario(owner.Journal, owner).ExecuteAsync(fixture.Operation(request), Token)).Kind);
        Assert.Equal(0, fixture.Writes);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task SessionExpiryBetweenVersionAndCategoryReadsRequestsAuthentication(HttpStatusCode status)
    {
        using var fixture = new ApiFixture("qbittorrent");
        await fixture.LoginAsync();
        fixture.CategoryReadStatus = status;
        var read = await fixture.Operation(OperationFixture.NewRequest()).ObserveAsync(Token);
        Assert.Equal(OperationErrorCode.AuthenticationRequired, read.Failure!.Code);
        Assert.Equal(OperationOutcomeKind.Invalid, read.Failure.Kind);
        Assert.Equal(0, fixture.Writes);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("radarr")]
    public async Task VersionAuthAndProfileFailuresAreReadOnly(string service)
    {
        using var fixture = new ApiFixture(service);
        await fixture.LoginAsync();
        fixture.WrongVersion = true;
        var operation = fixture.Operation(OperationFixture.NewRequest(service));
        var result = await operation.ObserveAsync(Token);
        Assert.Equal(OperationErrorCode.VersionUnsupported, result.Failure!.Code);
        fixture.WrongVersion = false;
        fixture.DenyReads = true;
        result = await operation.ObserveAsync(Token);
        Assert.Equal(OperationErrorCode.AuthenticationRequired, result.Failure!.Code);
        fixture.DenyReads = false;
        var request = OperationFixture.NewRequest(service);
        request = request with { Identity = request.Identity with { Resource = request.Identity.Resource with { ProfileFingerprint = new string('a', 64) } } };
        result = await fixture.Operation(request).ObserveAsync(Token);
        Assert.Equal(OperationErrorCode.IdentityMismatch, result.Failure!.Code);
        Assert.Equal(0, fixture.Writes);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("radarr")]
    public async Task ExistingConflictsAndChangesBeforeApplyNeverOverwrite(string service)
    {
        using var fixture = new ApiFixture(service) { ExistingPath = "/data/media/foreign" };
        await fixture.LoginAsync();
        var owner = new OperationFixture();
        var request = OperationFixture.NewRequest(service);
        Assert.Equal(OperationOutcomeKind.Conflict,
            (await new ConfigurationOperationScenario(owner.Journal, owner).ExecuteAsync(fixture.Operation(request), Token)).Kind);
        Assert.Null(owner.Journal.Entry);
        fixture.ExistingPath = null;
        var operation = fixture.Operation(request);
        var baseline = (await operation.ObserveAsync(Token)).Value!;
        fixture.ExistingPath = request.Path;
        var write = await operation.ApplyAsync(baseline, Token);
        Assert.Equal(OperationErrorCode.ObservationChanged, write.Failure!.Code);
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task ReplacedRootIdentityBetweenObserveAndVerifyCannotBeAdopted()
    {
        using var fixture = new ApiFixture("radarr") { ExistingPath = "/data/media/movies", ReplaceRootOnSecondRead = true };
        var owner = new OperationFixture();
        var result = await new ConfigurationOperationScenario(owner.Journal, owner).ExecuteAsync(fixture.Operation(OperationFixture.NewRequest("radarr")), Token);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Kind);
        Assert.Equal(OperationErrorCode.VerificationFailed, result.Code);
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task WrongServiceAndUnsafePathDoNotReachTheApi()
    {
        using var fixture = new ApiFixture("radarr");
        var mismatch = fixture.Operation(OperationFixture.NewRequest("sonarr"));
        Assert.Equal(OperationErrorCode.InvalidRequest, (await mismatch.ObserveAsync(Token)).Failure!.Code);
        var invalid = fixture.Operation(OperationFixture.NewRequest("radarr") with { Path = "/etc/foreign" });
        Assert.Equal(OperationErrorCode.InvalidRequest, (await invalid.ObserveAsync(Token)).Failure!.Code);
        Assert.Equal(0, fixture.Requests);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("radarr")]
    public async Task AClientCannotBeBoundToAnotherOwnedResourceEndpoint(string service)
    {
        using var fixture = new ApiFixture(service);
        var request = OperationFixture.NewRequest(service);
        request = request with { Identity = request.Identity with { Resource = request.Identity.Resource with { EndpointFingerprint = ConfigurationRequest.Fingerprint("http://127.0.0.1:12345/") } } };
        var read = await fixture.Operation(request).ObserveAsync(Token);
        Assert.Equal(OperationErrorCode.IdentityMismatch, read.Failure!.Code);
        Assert.Equal(0, fixture.Requests);
    }

    [Theory]
    [InlineData("qbittorrent")]
    [InlineData("radarr")]
    public async Task ReadTransportFailureIsSafeAndCannotStartAnIntent(string service)
    {
        using var fixture = new ApiFixture(service);
        await fixture.LoginAsync();
        fixture.ThrowOnRead = true;
        var owner = new OperationFixture();
        var result = await new ConfigurationOperationScenario(owner.Journal, owner).ExecuteAsync(fixture.Operation(OperationFixture.NewRequest(service)), Token);
        Assert.Equal(OperationOutcomeKind.Transport, result.Kind);
        Assert.Equal(OperationErrorCode.ReadFailed, result.Code);
        Assert.DoesNotContain("synthetic-secret", result.ToString());
        Assert.Null(owner.Journal.Entry);
        Assert.Equal(0, fixture.Writes);
    }

    private sealed class ApiFixture : HttpMessageHandler
    {
        private const string Key = "0123456789abcdef0123456789abcdef";

        private readonly string service;

        private readonly HttpClient http;

        private readonly QbittorrentContractClient? qbittorrent;

        private readonly ArrContractClient? arr;

        private bool loggedIn;

        private int rootIdentity = 1;

        public ApiFixture(string service)
        {
            this.service = service;
            http = new(this, disposeHandler: false) { BaseAddress = new("http://127.0.0.1:9876/") };
            if (service == "qbittorrent")
                qbittorrent = new(http);
            else
                arr = new(http, ArrService.Radarr, Key);
        }

        public string? ExistingPath { get; set; }

        public bool LoseResponse { get; set; }

        public bool ApplyOnServer { get; set; } = true;

        public bool WrongVersion { get; set; }

        public bool DenyReads { get; set; }

        public bool ThrowOnRead { get; set; }

        public bool ReplaceRootOnSecondRead { get; set; }

        public HttpStatusCode WriteStatus { get; set; } = HttpStatusCode.OK;

        public CancellationTokenSource? CancelOnWrite { get; set; }

        public CancellationTokenSource? CancelOnSecondResourceRead { get; set; }

        public HttpStatusCode? CategoryReadStatus { get; set; }

        public int Writes { get; private set; }

        public int Requests { get; private set; }

        public int ResourceReads { get; private set; }

        public Task LoginAsync() => qbittorrent?.LoginAsync("synthetic", "synthetic-password") ?? Task.CompletedTask;

        public IConfigurationOperation Operation(ConfigurationRequest request) => qbittorrent is not null
            ? new QbittorrentCategoryOperation(qbittorrent, request)
            : new ArrRootFolderOperation(arr!, request);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v2/auth/login")
            {
                loggedIn = true;
                return new(HttpStatusCode.NoContent);
            }
            if (service == "radarr")
                Assert.Equal(Key, Assert.Single(request.Headers.GetValues("X-Api-Key")));
            if (request.Method == HttpMethod.Get)
            {
                if (ThrowOnRead)
                    throw new HttpRequestException("synthetic-secret https://user:password@example.invalid");
                if (DenyReads || (service == "qbittorrent" && !loggedIn))
                    return new(HttpStatusCode.Forbidden);
                if (path == "/api/v2/app/version")
                    return Text(WrongVersion ? "v9.9.9" : "v5.2.4");
                if (path == "/api/v3/system/status")
                    return Text(new JsonObject { ["version"] = WrongVersion ? "9.9.9" : "6.4.4.10685" }.ToJsonString());
                ResourceReads++;
                if (ResourceReads == 2 && CancelOnSecondResourceRead is not null)
                {
                    CancelOnSecondResourceRead.Cancel();
                    throw new OperationCanceledException(token);
                }
                if (service == "qbittorrent" && CategoryReadStatus is not null)
                    return new(CategoryReadStatus.Value);
                if (service == "qbittorrent")
                    return Text(ExistingPath is null ? "{}" : new JsonObject { ["movies"] = new JsonObject { ["savePath"] = ExistingPath } }.ToJsonString());
                if (ReplaceRootOnSecondRead && ResourceReads == 2)
                    rootIdentity++;
                return Text(ExistingPath is null ? "[]" : new JsonArray(new JsonObject { ["id"] = rootIdentity, ["path"] = ExistingPath, ["accessible"] = true }).ToJsonString());
            }
            Writes++;
            var body = await request.Content!.ReadAsStringAsync(token);
            if (ApplyOnServer)
            {
                ExistingPath = service == "qbittorrent"
                    ? Uri.UnescapeDataString(body.Split('&').Single(part => part.StartsWith("savePath=", StringComparison.Ordinal))[9..])
                    : JsonNode.Parse(body)!["path"]!.GetValue<string>();
            }
            if (CancelOnWrite is not null)
            {
                CancelOnWrite.Cancel();
                throw new OperationCanceledException(token);
            }
            if (LoseResponse)
                throw new HttpRequestException("synthetic-secret-lost-response");
            return new(WriteStatus);
        }

        private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                http.Dispose();
            base.Dispose(disposing);
        }
    }
}
