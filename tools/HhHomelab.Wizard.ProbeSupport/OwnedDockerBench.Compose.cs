using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.ProbeSupport;

public sealed partial class OwnedDockerBench
{
    private const string ComposeWorkDirectory = "/tmp/wizard-compose";

    /// <summary>Places the given files below a fresh work directory of the disposable Docker fixture and runs
    /// `docker compose config` of its pinned Compose there. Config only parses and interpolates: nothing is
    /// pulled, created or started. Output is returned to the caller for structured checks, never printed.</summary>
    public async Task<(int ExitCode, string Output, string Error)> RunComposeConfigAsync(
        IReadOnlyList<(string RelativePath, byte[] Content)> files, string projectDirectory, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!ConfigurationFileRequest.IsRelativePath(projectDirectory + "/compose.yaml"))
            throw new InvalidOperationException("bench.compose.path.invalid");
        await RequireInnerDockerOwnershipAsync(token);
        await RunAsync(["exec", Container, "rm", "-rf", ComposeWorkDirectory], token);
        foreach (var (relativePath, content) in files)
        {
            if (!ConfigurationFileRequest.IsRelativePath(relativePath))
                throw new InvalidOperationException("bench.compose.path.invalid");
            using var input = new MemoryStream(content, writable: false);
            // The path travels as a positional argument, not inside the script text.
            var written = await RunProcessAsync(["--context", context, "exec", "--interactive", Container, "sh", "-c",
                "mkdir -p \"$(dirname \"$1\")\" && cat > \"$1\"", "sh", ComposeWorkDirectory + "/" + relativePath], token, input);
            if (written.ExitCode != 0)
                throw new InvalidOperationException("bench.compose.write.failed");
        }
        var directory = ComposeWorkDirectory + "/" + projectDirectory;
        return await RunProcessAsync(["--context", context, "exec", Container, "docker", "--host", "unix:///var/run/docker.sock",
            "compose", "--project-directory", directory, "--file", directory + "/compose.yaml", "config", "--format", "json"], token);
    }

    /// <summary>Absolute path of a relative path below the compose work directory, as Compose resolves it.</summary>
    public static string ComposeWorkPath(string relativePath) => ComposeWorkDirectory + "/" + relativePath;
}
