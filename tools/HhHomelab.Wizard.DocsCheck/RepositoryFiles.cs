using System.Diagnostics;

namespace HhHomelab.Wizard.DocsCheck;

/// <summary>Files Git would commit: tracked plus untracked but not ignored, so build output is never a link target.</summary>
public static class RepositoryFiles
{
    public static IReadOnlySet<string> List(string root)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("--cached");
        start.ArgumentList.Add("--others");
        start.ArgumentList.Add("--exclude-standard");
        start.ArgumentList.Add("-z");

        using var git = Process.Start(start) ?? throw new InvalidOperationException("git could not be started");
        var listed = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        if (git.ExitCode != 0)
            throw new InvalidOperationException("git ls-files failed");

        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in listed.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var existing = File.Exists(Path.Combine(root, entry));
            if (existing)
                files.Add(entry);
        }

        return files;
    }

    /// <summary>Nearest directory at or above <paramref name="start"/> that contains the solution file.</summary>
    public static string? FindRoot(string start)
    {
        var directory = new DirectoryInfo(start);
        while (directory is not null)
        {
            var solution = Path.Combine(directory.FullName, "HhHomelab.Wizard.slnx");
            if (File.Exists(solution))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }
}
