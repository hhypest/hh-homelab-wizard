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

using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
Console.CancelKeyPress += (_, signal) =>
{
    signal.Cancel = true;
    deadline.Cancel();
};

SshProbeCredentials credentials;
try
{
    credentials = await SecretInput.ReadCredentialsAsync(options, deadline.Token);
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
{
    Console.Error.WriteLine("nas-read-only.failure: credentials.unreadable");
    return 2;
}

using (credentials)
{
    var check = new ReadOnlyCheck(options, credentials, Console.Out);
    var exitCode = await check.RunAsync(deadline.Token);
    if (options.ReportPath is not null)
    {
        var json = JsonSerializer.Serialize(check.Report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(options.ReportPath, json + "\n", new UTF8Encoding(false));
        Console.WriteLine("report: " + options.ReportPath);
    }

    Console.WriteLine(exitCode == 0 ? "nas-read-only: completed; nothing on the NAS was changed" : "nas-read-only: stopped");
    return exitCode;
}
