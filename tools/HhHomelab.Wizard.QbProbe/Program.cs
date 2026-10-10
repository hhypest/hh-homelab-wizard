using HhHomelab.Wizard.Infrastructure;

// A developer probe: username/password arrive on stdin, never as process arguments.
if (args.Length != 1 || !Uri.TryCreate(args[0], UriKind.Absolute, out var endpoint))
{
    Console.Error.WriteLine("Usage: QbProbe <base-url-with-trailing-slash>; stdin: username newline password newline");
    return 2;
}
try
{
    using var handler = new HttpClientHandler { UseCookies = true, AllowAutoRedirect = false };
    using var http = new HttpClient(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(20) };
    var probe = new QbittorrentContractClient(http);
    var username = await Console.In.ReadLineAsync() ?? throw new InvalidOperationException("input.required");
    var password = await Console.In.ReadLineAsync() ?? throw new InvalidOperationException("input.required");
    await probe.LoginAsync(username, password);
    Console.WriteLine(await probe.GetVersionAsync());
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or HttpRequestException or TaskCanceledException)
{
    var code = exception is InvalidOperationException && exception.Message is
        "qb.authentication.rejected" or "qb.version.unavailable" or "qb.version.unsupported" or "input.required"
        ? exception.Message : "qb.probe.failed";
    Console.Error.WriteLine(code + "; credentials and server response are withheld.");
    return 1;
}
