using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using HhHomelab.Wizard.ProbeSupport;

/// <summary>The read-only compiler uses real SSH observations from the disposable fixture. The
/// fixture lacks DSM/Docker prerequisites, so only numeric identity checks can pass here.</summary>
internal static class ContainerIdentityScenarios
{
    internal static async Task RunAsync(OwnedDockerBench bench, SshProbeCredentials credentials,
        SshHostKey pin, CancellationToken token)
    {
        var compiler = new QbittorrentPlanCompiler(new PinnedQbittorrentPlanProfile(),
            new NasPreflightProbe(bench.SshTarget, credentials), new NasPathProbe(bench.SshTarget, credentials), new NasDockerNetworkProbe(bench.SshTarget, credentials));
        var installation = Guid.NewGuid();
        var request = Request(bench.SshTarget, installation, 1000, 1000);
        var matched = await CompileAsync(compiler, request, pin, token);
        Require(Has(matched, "plan.puid.ssh-user", PreflightCheckStatus.Passed), "user-match");
        Require(Has(matched, "plan.pgid.ssh-primary-group", PreflightCheckStatus.Passed), "group-match");
        Require(matched.HasBlockingChecks, "non-dsm-still-blocked");
        Require(Has(matched, "paths.dsm-acl", PreflightCheckStatus.NotChecked), "acl-not-claimed");

        var renewed = await compiler.RevalidateAsync(matched, pin, token);
        Require(renewed.Fresh.Value is not null && renewed.Unchanged, "fresh-revalidation");
        var wrongUser = await CompileAsync(compiler, Request(bench.SshTarget, installation, 1026, 1000), pin, token);
        Require(Has(wrongUser, "plan.puid.ssh-user.mismatch", PreflightCheckStatus.Blocking), "wrong-user-blocked");
        var wrongGroup = await CompileAsync(compiler, Request(bench.SshTarget, installation, 1000, 100), pin, token);
        Require(Has(wrongGroup, "plan.pgid.ssh-primary-group.mismatch", PreflightCheckStatus.Blocking), "wrong-group-blocked");
        Console.WriteLine("ssh-runtime.container-unix-identity: passed");
    }

    private static QbittorrentInstallationRequest Request(SshConnectionTarget target, Guid installation, int userId, int groupId) =>
        new(installation, target, new("/volume1/data", "/volume1/docker/hh-new"),
            "192.0.2.53", "0.0.0.0", 19080, 16881, "media-test", userId, groupId, "002", "Europe/Moscow", 1024, 512);

    private static async Task<QbittorrentInstallationPlan> CompileAsync(QbittorrentPlanCompiler compiler,
        QbittorrentInstallationRequest request, SshHostKey pin, CancellationToken token)
    {
        var result = await compiler.CompileAsync(request, pin, token);
        return result.Value ?? throw new SshScenarioException("container-identity.compile." + result.Failure?.Code);
    }

    private static bool Has(QbittorrentInstallationPlan plan, string code, PreflightCheckStatus status) =>
        plan.Checks.Any(check => check.Code == code && check.Status == status);

    private static void Require(bool satisfied, string code)
    {
        if (!satisfied)
            throw new SshScenarioException("container-identity." + code);
    }
}
