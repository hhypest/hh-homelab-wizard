using System.Net;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class HttpTransportRegressionTests
{
    private const string Sensitive = "synthetic-sensitive-value";
    private const string ServerId = "00000000000000000000000000000001";
    private const string UserId = "00000000000000000000000000000002";
    private const string Key = "0123456789abcdef0123456789abcdef";

    public enum Service
    {
        Qbittorrent,
        Radarr,
        Sonarr,
        Prowlarr,
        Jellyfin,
        Seerr,
    }

    public enum Fault
    {
        Deadline,
        Disconnect,
        BodyDisconnect,
        BodyDeadline,
    }

    public static IEnumerable<object[]> ReadFaults => Cases(includeProwlarr: true);

    public static IEnumerable<object[]> WriteFaults => Cases(includeProwlarr: false);

    private static IEnumerable<object[]> Cases(bool includeProwlarr)
    {
        foreach (var service in Enum.GetValues<Service>())
        {
            if (!includeProwlarr && service == Service.Prowlarr)
                continue;
            foreach (var fault in Enum.GetValues<Fault>())
                yield return [service, fault];
        }
    }

    [Theory]
    [MemberData(nameof(ReadFaults))]
    public async Task ReadFailureIsSanitizedTransport(Service service, Fault fault)
    {
        using var fixture = new Fixture(service);
        using var http = fixture.Client();
        var read = Reader(service, http);
        fixture.FailNext(fault);
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => read(CancellationToken.None));
        AssertSafe(error, ReadCode(service));
        Assert.Equal(1, fixture.Faults);
    }

    [Theory]
    [MemberData(nameof(WriteFaults))]
    public async Task FailureAfterMutationIsUnknownAndNeverRetried(Service service, Fault fault)
    {
        using var fixture = new Fixture(service);
        using var http = fixture.Client();
        var write = await WriterAsync(service, http);
        fixture.FailNext(fault, writesOnly: true);
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => write(CancellationToken.None));
        AssertSafe(error, WriteCode(service), ContractFailureKind.Unknown);
        Assert.Equal(1, fixture.Writes);
        Assert.Equal(1, fixture.Faults);
        fixture.ClearFault();
        await Reader(service, http)(CancellationToken.None);
        Assert.Equal(1, fixture.Writes);
    }

    [Theory]
    [InlineData(Fault.Deadline)]
    [InlineData(Fault.Disconnect)]
    [InlineData(Fault.BodyDeadline)]
    [InlineData(Fault.BodyDisconnect)]
    public async Task QbittorrentPreferencesWriteHasUnknownResult(Fault fault)
    {
        using var fixture = new Fixture(Service.Qbittorrent);
        using var http = fixture.Client();
        var qbittorrent = new QbittorrentContractClient(http);
        await qbittorrent.LoginAsync("wizard", Sensitive, TestContext.Current.CancellationToken);
        fixture.FailNext(fault, writesOnly: true);
        var desired = new QbittorrentDownloadSettings("/data/torrents/", "/data/torrents/incomplete/", true);
        var error = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
            qbittorrent.ConfigureDownloadsAsync(desired, TestContext.Current.CancellationToken));
        AssertSafe(error, "qb.preferences.result-unknown", ContractFailureKind.Unknown);
        Assert.Equal(1, fixture.Writes);
    }

    [Theory]
    [InlineData(Service.Qbittorrent)]
    [InlineData(Service.Radarr)]
    [InlineData(Service.Sonarr)]
    [InlineData(Service.Jellyfin)]
    [InlineData(Service.Seerr)]
    public async Task CancellationAfterMutationIsUnknown(Service service)
    {
        using var fixture = new Fixture(service);
        using var http = fixture.Client();
        http.Timeout = Timeout.InfiniteTimeSpan;
        using var cancellation = new CancellationTokenSource();
        var write = await WriterAsync(service, http);
        fixture.FailNext(Fault.Deadline, writesOnly: true);
        var pending = write(cancellation.Token);
        await fixture.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => pending);
        AssertSafe(error, WriteCode(service), ContractFailureKind.Unknown);
        Assert.Equal(1, fixture.Writes);
    }

    [Theory]
    [InlineData(Service.Qbittorrent)]
    [InlineData(Service.Radarr)]
    [InlineData(Service.Sonarr)]
    [InlineData(Service.Jellyfin)]
    [InlineData(Service.Seerr)]
    public async Task AlreadyCanceledOperationNeverSends(Service service)
    {
        using var fixture = new Fixture(service);
        using var http = fixture.Client();
        var write = await WriterAsync(service, http);
        var requests = fixture.Requests;
        var writes = fixture.Writes;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(service, http)(cancellation.Token));
        Assert.Equal(requests, fixture.Requests);
        Assert.Equal(writes, fixture.Writes);
    }

    [Theory]
    [InlineData(Service.Qbittorrent)]
    [InlineData(Service.Radarr)]
    [InlineData(Service.Sonarr)]
    [InlineData(Service.Prowlarr)]
    [InlineData(Service.Jellyfin)]
    [InlineData(Service.Seerr)]
    public async Task CallerCanCancelReadWithoutTransportFailure(Service service)
    {
        using var fixture = new Fixture(service);
        using var http = fixture.Client();
        http.Timeout = Timeout.InfiniteTimeSpan;
        using var cancellation = new CancellationTokenSource();
        fixture.FailNext(Fault.Deadline);
        var pending = Reader(service, http)(cancellation.Token);
        await fixture.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.DoesNotContain(Sensitive, error.ToString());
    }

    public static IEnumerable<object[]> QbittorrentFaults
    {
        get
        {
            foreach (var stage in new[] { "preflight", "login", "proof", "preferences", "categories" })
                foreach (var fault in Enum.GetValues<Fault>())
                    yield return [stage, fault];
        }
    }

    [Theory]
    [MemberData(nameof(QbittorrentFaults))]
    public async Task QbittorrentEndpointsNeverExposeRawNetworkErrors(string stage, Fault fault)
    {
        using var fixture = new Fixture(Service.Qbittorrent);
        using var http = fixture.Client();
        var qbittorrent = new QbittorrentContractClient(http);
        if (stage is "preferences" or "categories")
            await qbittorrent.LoginAsync("wizard", Sensitive, TestContext.Current.CancellationToken);
        fixture.FaultPath = stage switch
        {
            "login" => "/api/v2/auth/login",
            "preferences" => "/api/v2/app/preferences",
            "categories" => "/api/v2/torrents/categories",
            _ => "/api/v2/app/version",
        };
        fixture.OnlyAuthenticated = stage == "proof";
        fixture.FailNext(fault);
        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(async () =>
        {
            if (stage == "preferences")
                _ = await qbittorrent.GetDownloadSettingsAsync(TestContext.Current.CancellationToken);
            else if (stage == "categories")
                _ = await qbittorrent.GetCategoriesAsync(TestContext.Current.CancellationToken);
            else
                await qbittorrent.LoginAsync("wizard", Sensitive, TestContext.Current.CancellationToken);
        });
        var code = stage switch
        {
            "preflight" => "qb.authentication.preflight.transport-failed",
            "login" => "qb.authentication.result-unknown",
            "proof" => "qb.version.transport-failed",
            "preferences" => "qb.preferences.transport-failed",
            "categories" => "qb.categories.transport-failed",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };
        var kind = stage == "login" ? ContractFailureKind.Unknown : ContractFailureKind.Transport;
        AssertSafe(error, code, kind);
        Assert.Equal(1, fixture.Faults);
    }

    private static void AssertSafe(
        Exception error,
        string code,
        ContractFailureKind kind = ContractFailureKind.Transport
    )
    {
        Assert.Equal(code, error.Message);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Sensitive, error.ToString());
        Assert.DoesNotContain("http://", error.ToString());
        var observed = error switch
        {
            QbittorrentContractException qbittorrent => qbittorrent.Kind,
            ArrContractException arr => arr.Kind,
            MediaContractException media => media.Kind,
            _ => throw new InvalidOperationException("Expected a sanitized contract exception"),
        };
        Assert.Equal(kind, observed);
    }

    public static IEnumerable<object[]> UiFaults
    {
        get
        {
            foreach (var service in new[] { Service.Radarr, Service.Sonarr, Service.Prowlarr })
                foreach (var isWrite in new[] { false, true })
                    foreach (var fault in Enum.GetValues<Fault>())
                        yield return [service, isWrite, fault];
        }
    }

    [Theory]
    [MemberData(nameof(UiFaults))]
    public async Task ArrUiLoginSeparatesReadFailureFromUnknownWrite(Service service, bool isWrite, Fault fault)
    {
        using var fixture = new Fixture(service);
        using var http = fixture.Client();
        using var uiFixture = new Fixture(service) { IsUi = true, FaultPath = isWrite ? "/login" : "/" };
        using var ui = uiFixture.Client();
        uiFixture.FailNext(fault);
        var arr = new ArrContractClient(http, Arr(service), Key);
        var error = await Assert.ThrowsAsync<ArrContractException>(() =>
            arr.ConfigureAuthenticationAsync("wizard", Sensitive, ui, TestContext.Current.CancellationToken));
        var code = isWrite ? "arr.ui.result-unknown" : "arr.ui.transport-failed";
        var kind = isWrite ? ContractFailureKind.Unknown : ContractFailureKind.Transport;
        AssertSafe(error, code, kind);
        Assert.Equal(isWrite ? 1 : 0, uiFixture.Writes);
        Assert.Equal(0, fixture.Writes);
    }

    private static Func<CancellationToken, Task> Reader(Service service, HttpClient http) => service switch
    {
        Service.Qbittorrent => token => new QbittorrentContractClient(http).GetVersionAsync(token),
        Service.Jellyfin => token => new JellyfinContractClient(http).GetStartupCompletedAsync(token),
        Service.Seerr => token => new SeerrContractClient(http).GetVersionAsync(token),
        _ => token => new ArrContractClient(http, Arr(service), Key).GetVersionAsync(token),
    };

    private static async Task<Func<CancellationToken, Task>> WriterAsync(Service service, HttpClient http)
    {
        if (service == Service.Qbittorrent)
        {
            var qbittorrent = new QbittorrentContractClient(http);
            await qbittorrent.LoginAsync("wizard", Sensitive);
            return token => qbittorrent.EnsureCategoryAsync("movies", "/data/torrents/movies", token);
        }
        if (service == Service.Jellyfin)
            return token => new JellyfinContractClient(http).LoginAdministratorAsync("wizard", Sensitive, token);
        if (service == Service.Seerr)
            return token => new SeerrContractClient(http).LoginJellyfinAsync(
                new Uri("http://jellyfin:8096/"), ServerId, UserId, "wizard", Sensitive, token);
        return token => new ArrContractClient(http, Arr(service), Key).EnsureRootFolderAsync(
            "/data/media/movies", token);
    }

    private static ArrService Arr(Service service) => service switch
    {
        Service.Radarr => ArrService.Radarr,
        Service.Sonarr => ArrService.Sonarr,
        Service.Prowlarr => ArrService.Prowlarr,
        _ => throw new ArgumentOutOfRangeException(nameof(service)),
    };

    private static string ReadCode(Service service) => service switch
    {
        Service.Qbittorrent => "qb.version.transport-failed",
        Service.Jellyfin => "jellyfin.read.transport-failed",
        Service.Seerr => "seerr.read.transport-failed",
        _ => "arr.read.transport-failed",
    };

    private static string WriteCode(Service service) => service switch
    {
        Service.Qbittorrent => "qb.category.result-unknown",
        Service.Jellyfin => "jellyfin.login.result-unknown",
        Service.Seerr => "seerr.login.result-unknown",
        _ => "arr.root.result-unknown",
    };

    private sealed class Fixture(Service service) : HttpMessageHandler
    {
        private bool authenticated;
        private Fault? fault;
        private bool writesOnly;

        public int Writes { get; private set; }

        public int Requests { get; private set; }

        public int Faults { get; private set; }

        public string? FaultPath { get; set; }

        public bool OnlyAuthenticated { get; set; }

        public bool IsUi { get; set; }

        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public HttpClient Client() => new(this, disposeHandler: false)
        {
            BaseAddress = new Uri("http://synthetic.invalid/"),
            Timeout = TimeSpan.FromMilliseconds(500),
        };

        public void FailNext(Fault next, bool writesOnly = false)
        {
            fault = next;
            this.writesOnly = writesOnly;
            Writes = 0;
        }

        public void ClearFault() => fault = null;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token
        )
        {
            Requests++;
            var path = request.RequestUri!.AbsolutePath;
            var isWrite = request.Method != HttpMethod.Get;
            if (isWrite)
                Writes++;
            if (path is "/api/v2/auth/login" or "/login")
                authenticated = true;
            var pathMatches = FaultPath is null || FaultPath == path;
            var sessionMatches = !OnlyAuthenticated || authenticated;
            var methodMatches = !writesOnly || isWrite;
            if (fault is { } current && pathMatches && sessionMatches && methodMatches)
            {
                Faults++;
                Reached.TrySetResult();
                if (current == Fault.Disconnect)
                    throw new HttpRequestException("http://synthetic.invalid/?password=" + Sensitive);
                if (current == Fault.Deadline)
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new FaultContent(current) };
            }
            return Reply(path);
        }

        private HttpResponseMessage Reply(string path)
        {
            if (IsUi)
            {
                var status = authenticated && path == "/" ? HttpStatusCode.OK : HttpStatusCode.Found;
                var response = new HttpResponseMessage(status);
                response.Headers.Location = new Uri(authenticated ? "/" : "/login", UriKind.Relative);
                return response;
            }
            if (path == "/api/v2/auth/login")
                return new(HttpStatusCode.NoContent);
            if (path == "/api/v2/app/version")
                return authenticated ? Json("v5.2.4") : new(HttpStatusCode.Forbidden);
            if (path == "/api/v2/torrents/categories")
                return Json("{}");
            if (path == "/api/v2/app/preferences")
                return Json("{\"save_path\":\"/downloads/\",\"temp_path\":\"/downloads/incomplete/\",\"temp_path_enabled\":false}");
            if (path.EndsWith("rootfolder", StringComparison.Ordinal))
                return Json("[]");
            if (path.EndsWith("config/host", StringComparison.Ordinal))
                return Json(
                    "{\"authenticationMethod\":\"forms\",\"authenticationRequired\":\"enabled\",\"username\":\"wizard\"}");
            if (path == "/System/Info/Public")
                return Json("{\"Version\":\"12.2.0\",\"Id\":\"" + ServerId + "\",\"StartupWizardCompleted\":true}");
            if (path == "/api/v1/auth/me")
                return new(HttpStatusCode.Unauthorized);
            if (path == "/api/v1/settings/public")
                return Json("{\"mediaServerType\":4}");
            var version = service switch
            {
                Service.Radarr => "6.4.4.10685",
                Service.Sonarr => "4.0.20.3014",
                Service.Prowlarr => "2.6.5.5623",
                Service.Seerr => "3.5.0",
                _ => throw new InvalidOperationException("Unexpected fixture route"),
            };
            return Json("{\"version\":\"" + version + "\"}");
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body),
        };
    }

    private sealed class FaultContent(Fault fault) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken token
        )
        {
            if (fault == Fault.BodyDeadline)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new IOException(Sensitive);
        }
    }
}
