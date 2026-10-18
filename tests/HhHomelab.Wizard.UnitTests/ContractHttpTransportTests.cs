using System.Net;
using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class ContractHttpTransportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2147483648)]
    public void NonFiniteOrUnsupportedBudgetsAreRejected(long milliseconds)
    {
        var invalid = TimeSpan.FromMilliseconds(milliseconds);
        var valid = TimeSpan.FromSeconds(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContractHttpTimeouts(invalid, valid));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ContractHttpTimeouts(valid, invalid));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExplicitReadAndWriteBudgetCoversHeadersAndBody(bool write, bool body)
    {
        using var handler = new Handler(async (_, token) =>
        {
            if (!body)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new WaitingContent() };
        });
        using var http = Client(handler);
        var shortBudget = TimeSpan.FromMilliseconds(150);
        var longBudget = TimeSpan.FromSeconds(10);
        var budgets = write
            ? new ContractHttpTimeouts(longBudget, shortBudget)
            : new ContractHttpTimeouts(shortBudget, longBudget);
        var exchange = Transport(http, budgets);
        using var request = Request(write);
        var error = await Assert.ThrowsAsync<MediaContractException>(() =>
            exchange.ExchangeAsync(request, "test", ReadStatus, Token)
                .WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal(write ? ContractFailureKind.Unknown : ContractFailureKind.Transport, error.Kind);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShorterClientTimeoutAlsoBoundsResponseConsumer(bool write)
    {
        using var content = new TrackingContent("{}");
        using var handler = new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = Client(handler);
        http.Timeout = TimeSpan.FromMilliseconds(150);
        var exchange = Transport(http, new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));
        using var request = Request(write);
        var error = await Assert.ThrowsAsync<MediaContractException>(() =>
            exchange.ExchangeAsync(request, "test", async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return true;
            }, Token).WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal(write ? ContractFailureKind.Unknown : ContractFailureKind.Transport, error.Kind);
        Assert.True(content.Disposed);
        Assert.Equal(TimeSpan.FromMilliseconds(150), http.Timeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidJsonIsSanitizedAndResponseIsDisposed(bool write)
    {
        using var content = new TrackingContent("synthetic-sensitive-value");
        using var handler = new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        using var http = Client(handler);
        var exchange = Transport(http);
        using var request = Request(write);
        var error = await Assert.ThrowsAsync<MediaContractException>(() =>
            exchange.ExchangeAsync(request, "test", async (response, token) =>
            {
                await using var stream = await response.Content.ReadAsStreamAsync(token);
                return await JsonNode.ParseAsync(stream, cancellationToken: token);
            }, Token, invalidResponseCode: "legacy.response-invalid"));
        Assert.IsAssignableFrom<ContractException>(error);
        Assert.Equal(write ? "test.result-unknown" : "legacy.response-invalid", error.Code);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("synthetic-sensitive-value", error.ToString());
        Assert.True(content.Disposed);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task VersionSpecificRejectionKeepsCodeAndStatusWithoutRetryOrClientMutation()
    {
        using var content = new TrackingContent("synthetic-sensitive-value");
        using var handler = new Handler((request, _) =>
        {
            Assert.Equal("fixed", request.Headers.GetValues("X-Synthetic").Single());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = content });
        });
        using var http = Client(handler);
        http.DefaultRequestHeaders.Add("X-Synthetic", "fixed");
        var exchange = Transport(http);
        using var request = Request(write: true);
        var error = await Assert.ThrowsAsync<MediaContractException>(() =>
            exchange.ExchangeAsync<bool>(request, "test", (response, _) =>
                throw new MediaContractException("legacy.denied", response.StatusCode), Token));
        Assert.Equal("legacy.denied", error.Code);
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Equal(ContractFailureKind.Contract, error.Kind);
        Assert.True(content.Disposed);
        Assert.Equal(1, handler.Requests);
        Assert.Equal("fixed", http.DefaultRequestHeaders.GetValues("X-Synthetic").Single());
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyHaCallerCancellationIsExplicitAndDoesNotRetry(bool legacyHa)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new Handler(async (_, token) =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = Client(handler);
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var exchange = Transport(http, preserveCaller: legacyHa);
        using var request = Request(write: true);
        var pending = exchange.ExchangeAsync(request, "test", ReadStatus, caller.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        caller.Cancel();
        if (legacyHa)
        {
            var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.Equal(caller.Token, error.CancellationToken);
        }
        else
        {
            var error = await Assert.ThrowsAsync<MediaContractException>(() => pending);
            Assert.Equal(ContractFailureKind.Unknown, error.Kind);
        }
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData("qb")]
    [InlineData("radarr")]
    [InlineData("sonarr")]
    [InlineData("prowlarr")]
    [InlineData("jellyfin")]
    [InlineData("seerr")]
    [InlineData("ha")]
    public async Task AdaptersUseExplicitBudgetWithInfiniteHttpClientTimeout(string service)
    {
        using var handler = new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = Client(handler);
        var budgets = new ContractHttpTimeouts(TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(10));
        var pending = service switch
        {
            "qb" => (Task)new QbittorrentContractClient(http, budgets).GetVersionAsync(Token),
            "jellyfin" => new JellyfinContractClient(http, budgets).GetStartupCompletedAsync(Token),
            "seerr" => new SeerrContractClient(http, budgets).GetVersionAsync(Token),
            "ha" => ReadHaAsync(http, budgets),
            _ => new ArrContractClient(http, Enum.Parse<ArrService>(service, ignoreCase: true),
                "0123456789abcdef0123456789abcdef", budgets).GetVersionAsync(Token),
        };
        var error = await Assert.ThrowsAnyAsync<ContractException>(() =>
            pending.WaitAsync(TimeSpan.FromSeconds(5), Token));
        Assert.Equal(ContractFailureKind.Transport, error.Kind);
        Assert.Equal(1, handler.Requests);
    }

    private static async Task ReadHaAsync(HttpClient http, ContractHttpTimeouts budgets)
    {
        await using var ha = new HomeAssistantContractClient(http, () => new GreetingSocket(), budgets);
        await ha.BootstrapOwnerAsync("Wizard", "wizard", "synthetic-password", Token);
    }

    private static Task<HttpStatusCode> ReadStatus(HttpResponseMessage response, CancellationToken _) =>
        Task.FromResult(response.StatusCode);

    private static ContractHttpTransport Transport(
        HttpClient http,
        ContractHttpTimeouts? budgets = null,
        bool preserveCaller = false
    ) => new(http, static (code, status, kind) => new MediaContractException(code, status, kind),
        budgets, preserveCaller);

    private static HttpClient Client(HttpMessageHandler handler) => new(handler, disposeHandler: false)
    {
        BaseAddress = new Uri("http://synthetic.invalid/"),
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static HttpRequestMessage Request(bool write) => new(write ? HttpMethod.Post : HttpMethod.Get, "test");

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> reply
    ) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            return reply(request, token);
        }
    }

    private sealed class TrackingContent(string body) : StringContent(body)
    {
        public bool Disposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class WaitingContent : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken token
        ) =>
            Task.Delay(Timeout.InfiniteTimeSpan, token);
    }

    private sealed class GreetingSocket : IHomeAssistantSocket
    {
        public Task ConnectAsync(Uri endpoint, CancellationToken token) => Task.CompletedTask;

        public Task SendAsync(JsonObject message, CancellationToken token) => throw new NotSupportedException();

        public Task<JsonObject> ReceiveAsync(CancellationToken token) => Task.FromResult(new JsonObject
        {
            ["type"] = "auth_required",
            ["ha_version"] = "2026.10.0",
        });

        public void Dispose() { }
    }
}
