using System.Diagnostics;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class NasPathContentsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "wizard-path-contents-" + Guid.NewGuid().ToString("N"));

    public static bool ShellUnavailable => !OperatingSystem.IsLinux();

    [Theory(Skip = "Linux shell required.", SkipWhen = nameof(ShellUnavailable))]
    [InlineData(null, "empty")]
    [InlineData(".hidden", "not-empty")]
    [InlineData("\n", "not-empty")]
    [InlineData("broken-link", "not-empty")]
    public async Task ListingDistinguishesEmptyHiddenNewlineAndBrokenLinkEntries(string? name, string expected)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        if (name == "broken-link")
            File.CreateSymbolicLink(Path.Combine(directory, name), Path.Combine(root, "absent"));
        else if (name is not null)
            await File.WriteAllTextAsync(Path.Combine(directory, name), "", TestContext.Current.CancellationToken);

        Assert.Equal(expected, await InspectAsync(directory));
    }

    [Theory(Skip = "Linux shell required.", SkipWhen = nameof(ShellUnavailable))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedListingNeverBecomesEmptyEvenAfterPartialOutput(bool partialOutput)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("test.linux.required");

        var directory = Directory.CreateDirectory(Path.Combine(root, "data")).FullName;
        var binaries = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
        var find = Path.Combine(binaries, "find");
        await File.WriteAllTextAsync(find, "#!/bin/sh\n" + (partialOutput ? "printf 'partial\\n'\n" : "") + "exit 1\n",
            TestContext.Current.CancellationToken);
        File.SetUnixFileMode(find, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Regression for the former ls | head | wc pipeline: first ls succeeds, second fails,
        // but wc exits successfully and the old implementation incorrectly returned empty.
        var listing = Path.Combine(binaries, "ls");
        await File.WriteAllTextAsync(listing,
            "#!/bin/sh\nif [ -f \"$TEST_STATE\" ]; then exit 1; fi\n: > \"$TEST_STATE\"\nexit 0\n",
            TestContext.Current.CancellationToken);
        File.SetUnixFileMode(listing, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        Assert.Equal("unavailable", await InspectAsync(directory, binaries));
    }

    private async Task<string> InspectAsync(string directory, string? binaries = null)
    {
        var script = NasPathProtocol.Command;
        var start = script.IndexOf("contents_of()", StringComparison.Ordinal);
        var end = script.IndexOf("\ncanonical_of()", start, StringComparison.Ordinal);
        var function = script[start..end];
        var arguments = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        arguments.ArgumentList.Add("-c");
        arguments.ArgumentList.Add(function + "\ncontents_of \"$1\"");
        arguments.ArgumentList.Add("sh");
        arguments.ArgumentList.Add(directory);
        if (binaries is not null)
        {
            arguments.Environment["PATH"] = binaries + ":" + Environment.GetEnvironmentVariable("PATH");
            arguments.Environment["TEST_STATE"] = Path.Combine(root, "listing-state");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var process = new Process { StartInfo = arguments };
        Assert.True(process.Start());
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.Empty(await error);
            return await output;
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
