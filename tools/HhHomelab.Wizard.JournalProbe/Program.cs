using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;

return await JournalProbe.RunAsync(args);

internal static class JournalProbe
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            if (args is ["--worker", var mode])
                return await WorkerAsync(mode, deadline.Token);
            if (args.Length != 0)
                return 2;
            await ProbeAsync(deadline.Token);
            Console.WriteLine("journal.probe.passed");
            return 0;
        }
        catch
        {
            Console.Error.WriteLine("journal.probe.failed");
            return 1;
        }
    }

    private static async Task ProbeAsync(CancellationToken token)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wizard-journal-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var resource = new ResourceIdentity(Guid.NewGuid(), Guid.NewGuid(), "qbittorrent",
            PinnedRuntimeProfile.Fingerprint, ConfigurationRequest.Fingerprint("synthetic-local-endpoint"));
        var descriptor = new OperationDescriptor(new(Guid.NewGuid(), resource),
            ConfigurationOperationKind.QbittorrentCategory, ConfigurationRequest.Fingerprint("synthetic-category"));
        var input = new ProbeInput(directory, new(resource.InstallationId, Guid.NewGuid(), resource.ProfileFingerprint), descriptor);
        try
        {
            FileOperationJournal.Create(directory, input.Scope);
            await HoldThenKillAsync(input, "hold-intent", token);
            await ExpectWorkerAsync(input, "missing", "unknown:0", token);
            await HoldThenKillAsync(input, "hold-confirm", token);
            await ExpectWorkerAsync(input, "satisfied", "already-satisfied:0", token);
            Console.WriteLine("journal.probe.restart-and-exclusive-lock.passed");
        }
        finally
        {
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(Path.GetFullPath(directory)) != parent)
                throw new InvalidOperationException("probe.directory.invalid");
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task HoldThenKillAsync(ProbeInput input, string mode, CancellationToken token)
    {
        using var worker = await StartAsync(input, mode, token);
        try
        {
            Require(await worker.StandardOutput.ReadLineAsync(token) == "ready");
            // An independent process must fail to acquire the still-held installation lock.
            await ExpectWorkerAsync(input, "lock", "busy", token);
        }
        finally
        {
            if (!worker.HasExited)
                worker.Kill(entireProcessTree: true);
            await worker.WaitForExitAsync(CancellationToken.None);
        }
        Require(worker.ExitCode != 0);
    }

    private static async Task ExpectWorkerAsync(ProbeInput input, string mode, string expected, CancellationToken token)
    {
        using var worker = await StartAsync(input, mode, token);
        try
        {
            var output = await worker.StandardOutput.ReadToEndAsync(token);
            await worker.WaitForExitAsync(token);
            Require(worker.ExitCode == 0 && output.Trim() == expected);
        }
        finally
        {
            if (!worker.HasExited)
            {
                worker.Kill(entireProcessTree: true);
                await worker.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static async Task<Process> StartAsync(ProbeInput input, string mode, CancellationToken token)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("probe.host.missing");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--worker");
        start.ArgumentList.Add(mode);
        var process = Process.Start(start) ?? throw new InvalidOperationException("probe.start.failed");
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(input).AsMemory(), token);
            process.StandardInput.Close();
            return process;
        }
        catch
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            process.Dispose();
            throw;
        }
    }

    private static async Task<int> WorkerAsync(string mode, CancellationToken token)
    {
        var line = await Console.In.ReadLineAsync(token);
        var input = JsonSerializer.Deserialize<ProbeInput>(line ?? "")
            ?? throw new InvalidOperationException("probe.input.invalid");
        FileOperationJournal journal;
        try
        {
            journal = FileOperationJournal.Open(input.Directory, input.Scope);
        }
        catch (OperationLeaseUnavailableException) when (mode == "lock")
        {
            Console.WriteLine("busy");
            return 0;
        }
        if (mode == "lock")
            throw new InvalidOperationException("probe.lock.not-exclusive");
        if (mode.StartsWith("hold-", StringComparison.Ordinal))
        {
            if (mode == "hold-confirm")
            {
                var recovery = new SyntheticOperation(input.Descriptor, satisfied: true);
                var recovered = await new ConfigurationOperationScenario(journal, recovery).ExecuteAsync(recovery, token);
                Require(recovered.Kind == OperationOutcomeKind.Applied && recovery.ApplyCount == 0);
            }
            await using var lease = await journal.AcquireAsync(input.Descriptor, token);
            var intent = new OperationIntent(input.Descriptor,
                new(input.Descriptor, ObservedResourceState.Missing, new string('a', 64)));
            if (mode == "hold-intent")
                await journal.RecordIntentAsync(intent, token);
            else if (mode != "hold-confirm")
                throw new InvalidOperationException("probe.mode.invalid");
            Console.WriteLine("ready");
            await Console.Out.FlushAsync(token);
            await Task.Delay(Timeout.Infinite, token);
            return 1;
        }
        var operation = new SyntheticOperation(input.Descriptor, mode == "satisfied");
        var result = await new ConfigurationOperationScenario(journal, operation).ExecuteAsync(operation, token);
        var outcome = result.Kind switch
        {
            OperationOutcomeKind.Unknown => "unknown",
            OperationOutcomeKind.AlreadySatisfied => "already-satisfied",
            _ => "unexpected",
        };
        Console.WriteLine($"{outcome}:{operation.ApplyCount}");
        return 0;
    }

    private static void Require(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("probe.assertion.failed");
    }

    private sealed record ProbeInput(string Directory, OperationJournalScope Scope, OperationDescriptor Descriptor);

    private sealed class SyntheticOperation(OperationDescriptor descriptor, bool satisfied) : IConfigurationOperation, IOperationOwnership
    {
        public OperationDescriptor Descriptor { get; } = descriptor;

        public int ApplyCount { get; private set; }

        public Task<ResourceOwnership> CheckAsync(ResourceIdentity resource, CancellationToken token) => Task.FromResult(ResourceOwnership.Owned);

        public Task<OperationRead<OperationObservation>> ObserveAsync(CancellationToken token) => Task.FromResult(
            OperationRead<OperationObservation>.Observed(new(Descriptor,
                satisfied ? ObservedResourceState.Satisfied : ObservedResourceState.Missing, new string('b', 64))));

        public Task<OperationWriteAttempt> ApplyAsync(OperationObservation baseline, CancellationToken token)
        {
            ApplyCount++;
            throw new InvalidOperationException("probe.unexpected.apply");
        }

        public Task<OperationRead<OperationVerification>> VerifyAsync(CancellationToken token) => Task.FromResult(
            OperationRead<OperationVerification>.Observed(new(Descriptor, ObservedResourceState.Satisfied, new string('b', 64))));
    }
}
