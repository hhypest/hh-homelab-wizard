using System.Text;
using System.Text.Json;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.NasReadOnlyCheck;

var (options, error) = CheckOptions.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("nas-read-only.failure: " + error);
    Console.Error.WriteLine(CheckOptions.Usage);
    return 2;
}

// Secrets are typed before the deadline starts and before Ctrl+C is intercepted: time spent at the prompt
// does not shorten the check, and Ctrl+C at the prompt ends the process as usual.
SshProbeCredentials credentials;
try
{
    credentials = await SecretInput.ReadCredentialsAsync(options, CancellationToken.None);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine("nas-read-only.failure: credentials.unreadable");
    return 2;
}

using (credentials)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    Console.CancelKeyPress += (_, signal) =>
    {
        signal.Cancel = true;
        deadline.Cancel();
    };

    var check = new ReadOnlyCheck(options, credentials, Console.Out);
    var exitCode = await check.RunAsync(deadline.Token);
    if (options.ReportPath is not null && !await WriteReportAsync(options.ReportPath, check.Report))
        exitCode = exitCode == 0 ? 2 : exitCode;

    Console.WriteLine(exitCode == 0 ? "nas-read-only: completed; nothing on the NAS was changed" : "nas-read-only: stopped");
    return exitCode;
}

static async Task<bool> WriteReportAsync(string path, IReadOnlyDictionary<string, object?> report)
{
    var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    try
    {
        await File.WriteAllTextAsync(path, json + "\n", new UTF8Encoding(false));
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
        or NotSupportedException)
    {
        Console.Error.WriteLine("nas-read-only.failure: report.unwritable");
        return false;
    }

    Console.WriteLine("report: " + path);
    return true;
}
