using System.Net;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class QbittorrentContractTests
{
    [Theory]
    [InlineData("Fails.", 200)]
    [InlineData("Ok.", 200)]
    [InlineData("Ok.", 403)]
    [InlineData("", 401)]
    [InlineData("secret-in-response", 200)]
    public async Task DoesNotTreatHttp200AsAuthenticationAndDoesNotEchoBodies(string body, int status)
    {
        using var handler = new ProbeHandler((_, _) => new((HttpStatusCode)status) { Content = new StringContent(body) });
        using var http = NewClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new QbittorrentContractClient(http).LoginAsync("synthetic-user", "synthetic-password", TestContext.Current.CancellationToken));
        Assert.Equal("qb.authentication.rejected", exception.Message);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task SendsCredentialsInBodyWithSameOriginRefererAndChecksFollowUp()
    {
        using var handler = new ProbeHandler(async (request, cancellation) =>
        {
            Assert.DoesNotContain("synthetic-password", request.RequestUri!.AbsoluteUri);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("http://localhost:9080/", request.Headers.Referrer?.AbsoluteUri);
                Assert.Equal("/api/v2/auth/login", request.RequestUri.AbsolutePath);
                Assert.Equal("username=synthetic-user&password=synthetic-password", await request.Content!.ReadAsStringAsync(cancellation));
                return new(HttpStatusCode.NoContent);
            }
            Assert.Equal("/api/v2/app/version", request.RequestUri.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent("v5.2.4") };
        });
        using var http = NewClient(handler);
        await new QbittorrentContractClient(http).LoginAsync("synthetic-user", "synthetic-password", TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task SuccessfulLoginWithoutAuthenticatedFollowUpIsRejected()
    {
        using var handler = new ProbeHandler((request, _) => request.Method == HttpMethod.Post
            ? new(HttpStatusCode.NoContent)
            : new(HttpStatusCode.Forbidden));
        using var http = NewClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => new QbittorrentContractClient(http).LoginAsync("synthetic-user", "synthetic-password", TestContext.Current.CancellationToken));
        Assert.Equal("qb.version.unavailable", exception.Message);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task Version524NoContentLoginRequiresAuthenticatedFollowUp()
    {
        using var handler = new ProbeHandler((request, _) => request.Method == HttpMethod.Post
            ? new(HttpStatusCode.NoContent)
            : new(HttpStatusCode.OK) { Content = new StringContent("v5.2.4") });
        using var http = NewClient(handler);
        await new QbittorrentContractClient(http).LoginAsync("synthetic-user", "synthetic-password", TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task RejectsAServiceOutsideThePinnedVersion()
    {
        using var handler = new ProbeHandler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent("v5.2.5") });
        using var http = NewClient(handler);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new QbittorrentContractClient(http).GetVersionAsync(TestContext.Current.CancellationToken));
        Assert.Equal("qb.version.unsupported", exception.Message);
    }

    [Theory]
    [InlineData("http://user:password@localhost/")]
    [InlineData("http://localhost/?token=synthetic")]
    [InlineData("file:///etc/")]
    [InlineData("http://localhost/path")]
    public void RejectsCredentialsAndAmbiguousBaseAddresses(string address)
    {
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        Assert.Throws<ArgumentException>(() => new QbittorrentContractClient(http));
    }

    private static HttpClient NewClient(HttpMessageHandler handler) => new(handler) { BaseAddress = new("http://localhost:9080/") };

    private sealed class ProbeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public ProbeHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond)
            : this((request, cancellation) => Task.FromResult(respond(request, cancellation))) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return respond(request, cancellationToken);
        }
    }
}
