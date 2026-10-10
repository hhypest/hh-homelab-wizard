using System.Net;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class QbittorrentContractTests
{
    [Theory]
    [InlineData("Fails.", 200, "qb.authentication.http-error")]
    [InlineData("Ok.", 200, "qb.authentication.http-error")]
    [InlineData("Ok.", 403, "qb.authentication.forbidden")]
    [InlineData("", 401, "qb.authentication.rejected")]
    [InlineData("secret-in-response", 404, "qb.authentication.http-error")]
    [InlineData("secret-in-response", 500, "qb.authentication.http-error")]
    [InlineData("secret-in-response", 302, "qb.authentication.http-error")]
    public async Task PreservesFailureStatusWithoutEchoingBodies(string body, int status, string code)
    {
        using var handler = new ProbeHandler((request, _) => request.Method == HttpMethod.Get
            ? new(HttpStatusCode.Forbidden)
            : new((HttpStatusCode)status) { Content = new StringContent(body) });
        using var http = NewClient(handler);
        var exception = await Assert.ThrowsAsync<QbittorrentContractException>(() => new QbittorrentContractClient(http).LoginAsync("synthetic-user", "synthetic-password", TestContext.Current.CancellationToken));
        Assert.Equal(code, exception.Code);
        Assert.Equal(code, exception.Message);
        Assert.Equal((HttpStatusCode)status, exception.StatusCode);
        Assert.Equal(2, handler.Requests);
    }

    [Theory]
    [InlineData("existing-cookie-session")]
    [InlineData("localhost-authentication-bypass")]
    [InlineData("subnet-authentication-bypass")]
    public async Task DoesNotSubmitCredentialsWhenContextAlreadyHasAccess(string fixture)
    {
        using var handler = new ProbeHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v2/app/version", request.RequestUri!.AbsolutePath);
            return new(HttpStatusCode.OK) { Content = new StringContent(fixture) };
        });
        using var http = NewClient(handler);
        var exception = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
            new QbittorrentContractClient(http).LoginAsync("synthetic-user", "arbitrary-wrong-password", TestContext.Current.CancellationToken));
        Assert.Equal("qb.authentication.context-preauthenticated", exception.Code);
        Assert.Equal(HttpStatusCode.OK, exception.StatusCode);
        Assert.Equal(1, handler.Requests);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(500)]
    [InlineData(302)]
    [InlineData(204)]
    public async Task UnexpectedPreflightResponseStopsBeforeSendingCredentials(int status)
    {
        using var handler = new ProbeHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return new((HttpStatusCode)status) { Content = new StringContent("secret-in-response") };
        });
        using var http = NewClient(handler);
        var exception = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
            new QbittorrentContractClient(http).LoginAsync("synthetic-user", "synthetic-password", TestContext.Current.CancellationToken));
        Assert.Equal("qb.authentication.preflight-failed", exception.Code);
        Assert.Equal((HttpStatusCode)status, exception.StatusCode);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task SendsCredentialsInBodyWithSameOriginRefererAndChecksFollowUp()
    {
        var step = 0;
        using var handler = new ProbeHandler(async (request, cancellation) =>
        {
            step++;
            Assert.Equal("http://localhost:9080/", request.Headers.Referrer?.AbsoluteUri);
            Assert.DoesNotContain("synthetic-password", request.RequestUri!.AbsoluteUri);
            if (step == 2)
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/v2/auth/login", request.RequestUri.AbsolutePath);
                Assert.Equal("username=synthetic-user&password=synthetic-password", await request.Content!.ReadAsStringAsync(cancellation));
                return new(HttpStatusCode.NoContent);
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v2/app/version", request.RequestUri.AbsolutePath);
            return step == 1 ? new(HttpStatusCode.Forbidden)
                : new(HttpStatusCode.OK) { Content = new StringContent("v5.2.4") };
        });
        using var http = NewClient(handler);
        await new QbittorrentContractClient(http).LoginAsync("synthetic-user", "synthetic-password", TestContext.Current.CancellationToken);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task SuccessfulLoginWithoutAuthenticatedFollowUpIsRejected()
    {
        using var handler = new ProbeHandler((request, _) => request.Method == HttpMethod.Post
            ? new(HttpStatusCode.NoContent)
            : new(HttpStatusCode.Forbidden));
        using var http = NewClient(handler);
        var exception = await Assert.ThrowsAsync<QbittorrentContractException>(() => new QbittorrentContractClient(http).LoginAsync("synthetic-user", "synthetic-password", TestContext.Current.CancellationToken));
        Assert.Equal("qb.version.unavailable", exception.Message);
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task Version524NoContentLoginRequiresAuthenticatedFollowUp()
    {
        var step = 0;
        using var handler = new ProbeHandler((_, _) => ++step switch
        {
            1 => new(HttpStatusCode.Forbidden),
            2 => new(HttpStatusCode.NoContent),
            _ => new(HttpStatusCode.OK) { Content = new StringContent("v5.2.4") }
        });
        using var http = NewClient(handler);
        await new QbittorrentContractClient(http).LoginAsync("synthetic-user", "synthetic-password", TestContext.Current.CancellationToken);
        Assert.Equal(3, handler.Requests);

        // Reusing the now authenticated client must not validate an arbitrary new password.
        var exception = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
            new QbittorrentContractClient(http).LoginAsync("synthetic-user", "arbitrary-wrong-password", TestContext.Current.CancellationToken));
        Assert.Equal("qb.authentication.context-preauthenticated", exception.Code);
        Assert.Equal(4, handler.Requests);
    }

    [Fact]
    public async Task RejectsAServiceOutsideThePinnedVersion()
    {
        using var handler = new ProbeHandler((_, _) => new(HttpStatusCode.OK) { Content = new StringContent("v5.2.5") });
        using var http = NewClient(handler);
        var exception = await Assert.ThrowsAsync<QbittorrentContractException>(() =>
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
