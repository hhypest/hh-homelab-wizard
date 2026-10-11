using System.Text.Json.Nodes;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed partial class HomeAssistantContractTests
{
    private static Task<HomeAssistantJellyfinProgress> StartJellyfin(HomeAssistantContractClient ha) =>
        ha.StartJellyfinSetupAsync(new Uri("http://jellyfin:8096/"), new string('a', 32), "wizard", Token);

    [Fact]
    public async Task JellyfinRejectedCredentialsPermitExplicitCorrectionAndLoadedReadbackNeverWritesAgain()
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        Assert.Equal(HomeAssistantJellyfinStage.AwaitingCredentials, (await StartJellyfin(ha)).Stage);
        Assert.Equal(HomeAssistantJellyfinStage.AuthenticationRejected, (await ha.SubmitJellyfinCredentialsAsync("synthetic-wrong-password", Token)).Stage);
        var loaded = await ha.SubmitJellyfinCredentialsAsync("synthetic-password", Token);
        Assert.Equal(HomeAssistantJellyfinStage.Loaded, loaded.Stage); Assert.Equal("entry1", loaded.EntryId);
        await ha.ReconnectAsync(Token);
        Assert.Equal(loaded, await StartJellyfin(ha)); Assert.Equal(loaded, await ha.SubmitJellyfinCredentialsAsync("synthetic-password", Token));
        Assert.Equal(1, f.FlowStarts); Assert.Equal(2, f.FlowSubmits);
        Assert.DoesNotContain("synthetic", loaded.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostStartOrCallerCancellationNeverStartsSecondFlow(bool cancelled)
    {
        using var f = new Fixture { LoseFlow = "/api/config/config_entries/flow", CancelFlow = cancelled }; using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        using var caller = new CancellationTokenSource(); f.CallerToCancel = caller;
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ha.StartJellyfinSetupAsync(new("http://jellyfin:8096/"), new('a', 32), "wizard", caller.Token));
        else { var e = await Assert.ThrowsAsync<MediaContractException>(() => StartJellyfin(ha)); Assert.Equal("ha.jellyfin.start.result-unknown", e.Code); }
        Assert.Equal(HomeAssistantJellyfinStage.OutcomeUnknown, (await StartJellyfin(ha)).Stage);
        Assert.Equal(HomeAssistantJellyfinStage.OutcomeUnknown, (await ha.SubmitJellyfinCredentialsAsync("synthetic-password", Token)).Stage);
        Assert.Equal(1, f.FlowStarts); Assert.Equal(0, f.FlowSubmits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostSubmitCannotAdoptCreatedEntryOrRepeatRejectedCredentials(bool succeeds)
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha); await StartJellyfin(ha);
        f.LoseFlow = "/api/config/config_entries/flow/flow1";
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.SubmitJellyfinCredentialsAsync(succeeds ? "synthetic-password" : "synthetic-wrong-password", Token));
        Assert.Equal("ha.jellyfin.submit.result-unknown", e.Code);
        Assert.Equal(HomeAssistantJellyfinStage.OutcomeUnknown, (await ha.ObserveJellyfinSetupAsync(Token)).Stage);
        Assert.Equal(HomeAssistantJellyfinStage.OutcomeUnknown, (await ha.SubmitJellyfinCredentialsAsync("synthetic-password", Token)).Stage);
        Assert.Equal(1, f.FlowSubmits); Assert.Equal(succeeds ? 1 : 0, f.IntegrationEntries.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledSubmitKeepsUnknownIntentAndNeverResubmits(bool succeeds)
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha); await StartJellyfin(ha);
        using var caller = new CancellationTokenSource(); f.CallerToCancel = caller; f.CancelFlow = true; f.LoseFlow = "/api/config/config_entries/flow/flow1";
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ha.SubmitJellyfinCredentialsAsync(succeeds ? "synthetic-password" : "synthetic-wrong-password", caller.Token));
        Assert.Equal(HomeAssistantJellyfinStage.OutcomeUnknown, (await ha.SubmitJellyfinCredentialsAsync("synthetic-password", Token)).Stage);
        Assert.Equal(1, f.FlowSubmits);
    }

    [Fact]
    public async Task ExistingSameTitleEntryBlocksNewClientButReadOnlyIdentityCheckIsAllowed()
    {
        using var f = new Fixture(); using var h = f.Http(); await using var first = f.Client(h); await Bootstrap(first); await StartJellyfin(first);
        var loaded = await first.SubmitJellyfinCredentialsAsync("synthetic-password", Token);
        using var h2 = f.Http(); await using var second = f.Client(h2); await Bootstrap(second);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => StartJellyfin(second)); Assert.Equal("ha.jellyfin.existing-entry", e.Code);
        Assert.Equal(loaded, await second.ObserveJellyfinEntryAsync(loaded.EntryId!, new('a', 32), Token)); Assert.Equal(1, f.FlowStarts);
    }

    [Theory]
    [InlineData("server")]
    [InlineData("disabled")]
    [InlineData("failed")]
    public async Task EntryReadbackRejectsWrongServerDisabledOrFailedSetup(string reason)
    {
        using var f = new Fixture(); using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha); await StartJellyfin(ha);
        if (reason == "server") f.ServerId = new('b', 32);
        if (reason == "disabled") f.EntryDisabled = "user";
        if (reason == "failed") f.EntryState = "setup_error";
        var e = await Assert.ThrowsAsync<MediaContractException>(() => ha.SubmitJellyfinCredentialsAsync("synthetic-password", Token));
        Assert.Equal(reason switch { "server" => "ha.jellyfin.server-identity-mismatch", "disabled" => "ha.jellyfin.entry-disabled-or-conflict", _ => "ha.jellyfin.setup-failed" }, e.Code);
    }

    [Fact]
    public async Task SetupRetryIsPendingAndBecomesLoadedWithoutSecondSubmit()
    {
        using var f = new Fixture { EntryState = "setup_retry" }; using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha); await StartJellyfin(ha);
        Assert.Equal(HomeAssistantJellyfinStage.AwaitingSetup, (await ha.SubmitJellyfinCredentialsAsync("synthetic-password", Token)).Stage);
        f.IntegrationEntries[0]!["state"] = "loaded";
        Assert.Equal(HomeAssistantJellyfinStage.Loaded, (await ha.ObserveJellyfinSetupAsync(Token)).Stage); Assert.Equal(1, f.FlowSubmits);
    }

    [Fact]
    public async Task UnexpectedFlowAndForeignTargetFailWithoutCredentialsSubmission()
    {
        using var f = new Fixture { FlowHandler = "foreign" }; using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha);
        var e = await Assert.ThrowsAsync<MediaContractException>(() => StartJellyfin(ha)); Assert.Equal("ha.jellyfin.interaction-unsupported", e.Code);
        e = await Assert.ThrowsAsync<MediaContractException>(() => ha.StartJellyfinSetupAsync(new("http://other:8096/"), new('a', 32), "wizard", Token));
        Assert.Equal("ha.jellyfin.intent-conflict", e.Code); Assert.Equal(0, f.FlowSubmits); Assert.DoesNotContain("synthetic-sensitive", e.ToString());
    }

    [Theory]
    [InlineData("cannot_connect", HomeAssistantJellyfinStage.CannotConnect)]
    [InlineData("invalid_auth", HomeAssistantJellyfinStage.AuthenticationRejected)]
    public async Task KnownFlowErrorsReturnSafeStatuses(string error, HomeAssistantJellyfinStage expected)
    {
        using var f = new Fixture { FlowError = error }; using var h = f.Http(); await using var ha = f.Client(h); await Bootstrap(ha); await StartJellyfin(ha);
        Assert.Equal(expected, (await ha.SubmitJellyfinCredentialsAsync("synthetic-password", Token)).Stage);
    }
}
