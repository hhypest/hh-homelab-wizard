using System.Net;
using System.Security.Cryptography;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

const string pinnedImage = "lscr.io/linuxserver/qbittorrent:5.2.4_v2.0.15-ls479";
if (args.Length != 1)
{
    Console.Error.WriteLine(
        "Usage: QbDockerProbe <research-profile.json>; requires local Linux Docker and cached pinned image."
    );
    return 2;
}

using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};
var token = cancellation.Token;
OwnedDockerBench? bench = null;
var exitCode = 1;
try
{
    var profile = ProfileReader.Parse(await File.ReadAllTextAsync(args[0], token));
    if (
        profile.Source.Tag != "v1.3.0"
        || profile.Source.Commit != "b05047b081f3543520582315002e359ef1d93e59"
        || profile.Services.Single(s => s.Id == "qbittorrent").Image != pinnedImage
    )
        throw new InvalidOperationException("bench.profile.unsupported");
    bench = await OwnedDockerBench.ConnectAsync(token);
    await bench.StartAsync(pinnedImage, token);
    var temporaryPassword = await bench.ReadTemporaryPasswordAsync(token);
    var permanentPassword = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    var settings = new QbittorrentDownloadSettings(
        "/data/torrents",
        "/data/torrents/incomplete",
        true
    );

    using (var http = CreateClient(bench.Endpoint, simulateResponseLoss: true))
    {
        var api = new QbittorrentContractClient(http);
        await api.LoginAsync("admin", temporaryPassword, token);
        Console.WriteLine("temporary-login: passed; v5.2.4");
        await AssertInvalidHostPortAsync(bench.Endpoint, token);

        if (
            !await api.ConfigureDownloadsAsync(settings, token)
            || await api.ConfigureDownloadsAsync(settings, token)
        )
            throw new InvalidOperationException("bench.preferences.repeat.failed");
        Console.WriteLine("download-settings: applied and repeat skipped");

        try
        {
            await api.EnsureCategoryAsync("movies", "/data/torrents/movies", token);
            throw new InvalidOperationException("bench.response-loss.not-injected");
        }
        catch (QbittorrentContractException exception)
            when (exception.Code == "qb.category.result-unknown") { }
        // Observe the server after a deliberately dropped response; never repeat POST blindly.
        if (await api.EnsureCategoryAsync("movies", "/data/torrents/movies", token))
            throw new InvalidOperationException("bench.category.reconciliation.failed");
        if (
            !await api.EnsureCategoryAsync("tv", "/data/torrents/tv", token)
            || await api.EnsureCategoryAsync("tv", "/data/torrents/tv", token)
        )
            throw new InvalidOperationException("bench.category.repeat.failed");
        try
        {
            await api.EnsureCategoryAsync("movies", "/data/torrents/foreign", token);
            throw new InvalidOperationException("bench.category.conflict.not-detected");
        }
        catch (QbittorrentContractException exception)
            when (exception.Code == "qb.category.conflict") { }
        Console.WriteLine(
            "categories: movies/tv verified; response loss reconciled; conflict preserved"
        );
        await api.ChangeCredentialsAsync("wizard_m0", permanentPassword, token);
    }

    using (var wrong = CreateClient(bench.Endpoint))
    {
        try
        {
            await new QbittorrentContractClient(wrong).LoginAsync(
                "admin",
                temporaryPassword,
                token
            );
            throw new InvalidOperationException("bench.old-credentials.accepted");
        }
        catch (QbittorrentContractException exception)
            when (exception.Code == "qb.authentication.rejected"
                && exception.StatusCode == HttpStatusCode.Unauthorized
            ) { }
    }
    await VerifyConfiguredStateAsync(bench.Endpoint, permanentPassword, settings, token);
    Console.WriteLine("credentials: new login passed; old credentials rejected");
    await bench.RestartAsync(token);
    await VerifyConfiguredStateAsync(bench.Endpoint, permanentPassword, settings, token);
    Console.WriteLine("restart: credentials, paths and categories persisted; repeat skipped");
    exitCode = 0;
}
catch (QbittorrentContractException exception)
{
    Console.Error.WriteLine(
        exception.Code + (exception.StatusCode is { } status ? "; HTTP " + (int)status : "")
    );
}
catch (Exception exception)
{
    // Print only codes constructed in this tool, never external exception messages or streams.
    Console.Error.WriteLine(
        exception is InvalidOperationException
        && exception.Message.StartsWith("bench.", StringComparison.Ordinal)
            ? exception.Message
        : exception is OperationCanceledException ? "bench.cancelled-or-timeout"
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

static HttpClient CreateClient(Uri endpoint, bool simulateResponseLoss = false)
{
    HttpMessageHandler handler = new HttpClientHandler
    {
        UseCookies = true,
        AllowAutoRedirect = false,
        UseProxy = false,
    };
    if (simulateResponseLoss)
        handler = new LoseCategoryResponseHandler(handler);
    return new HttpClient(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(10) };
}

static async Task VerifyConfiguredStateAsync(
    Uri endpoint,
    string password,
    QbittorrentDownloadSettings settings,
    CancellationToken token
)
{
    using var http = CreateClient(endpoint);
    var api = new QbittorrentContractClient(http);
    await api.LoginAsync("wizard_m0", password, token);
    var observed = await api.GetDownloadSettingsAsync(token);
    var categories = await api.GetCategoriesAsync(token);
    var settingsDiffer = !settings.Matches(observed);
    var moviesMissing =
        !categories.TryGetValue("movies", out var movies)
        || movies.TrimEnd('/') != "/data/torrents/movies";
    var tvMissing =
        !categories.TryGetValue("tv", out var tv) || tv.TrimEnd('/') != "/data/torrents/tv";
    if (settingsDiffer || categories.Count != 2 || moviesMissing || tvMissing)
        throw new InvalidOperationException("bench.persisted-state.mismatch");
    if (await api.ConfigureDownloadsAsync(settings, token))
        throw new InvalidOperationException("bench.persisted-state.mismatch");
    if (await api.EnsureCategoryAsync("movies", "/data/torrents/movies", token))
        throw new InvalidOperationException("bench.persisted-state.mismatch");
    if (await api.EnsureCategoryAsync("tv", "/data/torrents/tv", token))
        throw new InvalidOperationException("bench.persisted-state.mismatch");
}

static async Task AssertInvalidHostPortAsync(Uri endpoint, CancellationToken token)
{
    using var http = CreateClient(endpoint);
    using var request = new HttpRequestMessage(HttpMethod.Get, "api/v2/app/version");
    var wrongPort = endpoint.Port == 9080 ? 9081 : 9080;
    request.Headers.Host = "127.0.0.1:" + wrongPort;
    request.Headers.Referrer = new Uri("http://" + request.Headers.Host + "/");
    using var response = await http.SendAsync(request, token);
    if (response.StatusCode != HttpStatusCode.Unauthorized)
        throw new InvalidOperationException("bench.host-port-policy.unexpected");
    Console.WriteLine("mismatched-Host-port: rejected with HTTP 401 without credentials");
}

internal sealed class LoseCategoryResponseHandler(HttpMessageHandler inner)
    : DelegatingHandler(inner)
{
    private bool injected;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (
            !injected
            && request.Method == HttpMethod.Post
            && request.RequestUri!.AbsolutePath == "/api/v2/torrents/createCategory"
            && response.StatusCode == HttpStatusCode.OK
        )
        {
            injected = true;
            response.Dispose();
            throw new HttpRequestException("simulated-response-loss");
        }
        return response;
    }
}
