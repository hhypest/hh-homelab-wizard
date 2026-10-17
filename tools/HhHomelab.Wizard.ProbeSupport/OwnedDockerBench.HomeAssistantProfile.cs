using System.Text.Json;
using HhHomelab.Wizard.Infrastructure;

namespace HhHomelab.Wizard.ProbeSupport;

public sealed partial class OwnedDockerBench
{
    /// <summary>Validate real YAML and templates in HA's runtime. Does not start HA or control hardware.</summary>
    public async Task CheckHomeAssistantProfileAsync(
        HomeAssistantPayload payload,
        CancellationToken token
    )
    {
        const string image = "ghcr.io/home-assistant/home-assistant:2026.10.0";
        if (resources.Count != 0)
            throw new InvalidOperationException("bench.profile.unsupported");
        _ = await RunAsync(["image", "inspect", image, "--format", "{{.Id}}"], token);
        await CreateNetworkAsync(token);
        await SeedHomeAssistantPayloadAsync(payload, image, token);
        resources.Add(("container", Container));
        _ = await RunAsync(
            [
                "create",
                "--interactive",
                "--name",
                Container,
                "--label",
                Label + "=" + session,
                "--network",
                Network,
                "--mount",
                $"type=volume,source={Volume},target=/config",
                "--entrypoint",
                "python3",
                image,
                "-",
            ],
            token
        );
        await VerifyOwnershipAsync("container", Container, token);
        using var script = typeof(OwnedDockerBench).Assembly.GetManifestResourceStream(
            "ha-profile-probe.py"
        )!;
        var result = await RunProcessAsync(
            ["--context", context, "start", "--attach", "--interactive", Container],
            token,
            script,
            TimeSpan.FromMinutes(4)
        );
        // Never expose raw HA output/errors: they may contain secret values or rendered config.
        using var json = JsonDocument.Parse(result.Output);
        var stages = new[] { "yaml", "configuration", "monitor_import", "templates" };
        foreach (var stage in stages)
        {
            var status = json.RootElement.GetProperty(stage).GetString();
            if (status is not ("passed" or "failed"))
                throw new InvalidOperationException("bench.ha.profile.result.invalid");
            Console.WriteLine("ha-profile." + stage + ": " + status);
        }
        if (json.RootElement.TryGetProperty("template_cases", out var cases))
            Console.WriteLine("ha-profile.template_cases: " + cases.GetInt32());
        if (
            json.RootElement.TryGetProperty("error_kind", out var kind)
            && kind.GetString()
                is "TypeError"
                    or "AttributeError"
                    or "KeyError"
                    or "AssertionError"
                    or "ImportError"
                    or "ModuleNotFoundError"
                    or "other"
        )
            Console.WriteLine("ha-profile.error_kind: " + kind.GetString());
        if (
            result.ExitCode != 0
            || stages.Any(s => json.RootElement.GetProperty(s).GetString() != "passed")
        )
            throw new InvalidOperationException("bench.ha.profile.validation.failed");
    }
}
