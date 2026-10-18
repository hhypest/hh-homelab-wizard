using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class ConfigurationOperationTests
{
    [Fact]
    public async Task FreshOperationPersistsIntentBeforeApplyAndConfirmsOnlyAfterIndependentVerify()
    {
        var fixture = new OperationFixture();
        var result = await fixture.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcomeKind.Applied, result.Kind);
        Assert.Equal(new[] { "lock", "read-journal", "ownership", "observe", "ownership", "intent", "ownership", "apply", "ownership", "verify", "ownership", "confirm", "unlock" }, fixture.Events);
        Assert.NotNull(fixture.Journal.Entry!.Confirmation);
    }

    [Fact]
    public async Task ExistingConfigurationNeedsTwoReadsButNoIntentOrWrite()
    {
        var fixture = new OperationFixture { State = ObservedResourceState.Satisfied };
        Assert.Equal(OperationOutcomeKind.AlreadySatisfied, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(0, fixture.ApplyCount);
        Assert.Null(fixture.Journal.Entry);
        Assert.Contains("verify", fixture.Events);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreviousIntentNeverAllowsApplyEvenWhenServerShowsNothing(bool appliedOnServer)
    {
        var fixture = new OperationFixture { WriteFailure = OperationResult.Failed(OperationOutcomeKind.Unknown, OperationErrorCode.WriteUncertain), ApplyChangesState = appliedOnServer };
        Assert.Equal(OperationOutcomeKind.Unknown, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.DoesNotContain("verify", fixture.Events);
        fixture.WriteFailure = null;
        var next = await fixture.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(appliedOnServer ? OperationOutcomeKind.Applied : OperationOutcomeKind.Unknown, next.Kind);
        Assert.Equal(1, fixture.ApplyCount);
    }

    [Fact]
    public async Task ConfirmedEntryIsObservedAgainWithoutReapplying()
    {
        var fixture = new OperationFixture();
        await fixture.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcomeKind.AlreadySatisfied, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(1, fixture.ApplyCount);
    }

    [Fact]
    public async Task ConfirmedOwnershipLossIsAConflictWithoutAReconciliationPrompt()
    {
        var fixture = new OperationFixture();
        await fixture.RunAsync(TestContext.Current.CancellationToken);
        fixture.Ownership = ResourceOwnership.Foreign;
        var result = await fixture.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Kind);
        Assert.Equal(OperationErrorCode.OwnershipNotProven, result.Code);
        Assert.Equal(1, fixture.ApplyCount);
    }

    [Fact]
    public async Task ConfirmedConfigurationRemovedLaterRequiresPlanReviewRatherThanAnotherApply()
    {
        var fixture = new OperationFixture();
        await fixture.RunAsync(TestContext.Current.CancellationToken);
        fixture.State = ObservedResourceState.Missing;
        var result = await fixture.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Kind);
        Assert.Equal(OperationErrorCode.ObservationChanged, result.Code);
        Assert.Equal(1, fixture.ApplyCount);
    }

    [Fact]
    public async Task ConfirmedReadFailureDoesNotImplyAnUnconfirmedWrite()
    {
        var fixture = new OperationFixture();
        await fixture.RunAsync(TestContext.Current.CancellationToken);
        fixture.ReadFailure = OperationResult.Failed(OperationOutcomeKind.Transport, OperationErrorCode.ReadFailed);
        Assert.Equal(OperationOutcomeKind.Transport, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(1, fixture.ApplyCount);
    }

    [Theory]
    [InlineData(ResourceOwnership.Foreign, OperationOutcomeKind.Conflict)]
    [InlineData(ResourceOwnership.Unproven, OperationOutcomeKind.Invalid)]
    public async Task OwnershipBlocksApiAndJournalWrites(ResourceOwnership ownership, OperationOutcomeKind expected)
    {
        var fixture = new OperationFixture { Ownership = ownership };
        Assert.Equal(expected, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.DoesNotContain("observe", fixture.Events);
        Assert.Equal(0, fixture.ApplyCount);
        Assert.Null(fixture.Journal.Entry);
    }

    [Fact]
    public async Task OwnershipIsRecheckedBeforeIntentAndAfterApply()
    {
        var fixture = new OperationFixture();
        fixture.OnOwnership = count => count == 2 ? ResourceOwnership.Foreign : ResourceOwnership.Owned;
        Assert.Equal(OperationOutcomeKind.Conflict, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Null(fixture.Journal.Entry);
        fixture.OnOwnership = count => count >= 4 ? ResourceOwnership.Unproven : ResourceOwnership.Owned;
        fixture.ResetOwnershipCount();
        Assert.Equal(OperationOutcomeKind.Unknown, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Null(fixture.Journal.Entry!.Confirmation);
    }

    [Theory]
    [InlineData(OperationOutcomeKind.Rejected, OperationErrorCode.WriteRejected)]
    [InlineData(OperationOutcomeKind.Unknown, OperationErrorCode.WriteUncertain)]
    [InlineData(OperationOutcomeKind.Conflict, OperationErrorCode.ObservationChanged)]
    [InlineData(OperationOutcomeKind.Invalid, OperationErrorCode.InvalidRequest)]
    [InlineData(OperationOutcomeKind.Transport, OperationErrorCode.ReadFailed)]
    public async Task TypedApplyFailuresDoNotTriggerRetryOrConfirmation(OperationOutcomeKind kind, OperationErrorCode code)
    {
        var fixture = new OperationFixture { WriteFailure = OperationResult.Failed(kind, code), ApplyChangesState = false };
        var result = await fixture.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(code, result.Code);
        Assert.Equal(1, fixture.ApplyCount);
        Assert.DoesNotContain("verify", fixture.Events);
        Assert.Null(fixture.Journal.Entry!.Confirmation);
    }

    [Fact]
    public async Task InvalidOrMismatchedEvidenceCannotAuthorizeWrite()
    {
        var fixture = new OperationFixture { Revision = "invalid" };
        Assert.Equal(OperationOutcomeKind.Conflict, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(0, fixture.ApplyCount);
        fixture.Revision = new string('a', 64);
        fixture.State = ObservedResourceState.Conflict;
        Assert.Equal(OperationOutcomeKind.Conflict, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(0, fixture.ApplyCount);
    }

    [Fact]
    public async Task DifferentRequestResourceOrProfileCannotResumeRecordedIntent()
    {
        var fixture = new OperationFixture { WriteFailure = OperationResult.Failed(OperationOutcomeKind.Unknown, OperationErrorCode.WriteUncertain) };
        var originalRequest = fixture.Request;
        await fixture.RunAsync(TestContext.Current.CancellationToken);
        var original = fixture.Journal.Entry!;
        var resource = fixture.Request.Identity.Resource with { ResourceId = Guid.NewGuid() };
        fixture.Request = fixture.Request with { Identity = fixture.Request.Identity with { Resource = resource } };
        Assert.Equal(OperationOutcomeKind.Conflict, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        fixture.Journal.Entry = original;
        fixture.Request = fixture.Request with { Path = "/data/torrents/changed" };
        Assert.Equal(OperationOutcomeKind.Conflict, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        fixture.Request = originalRequest with { Identity = originalRequest.Identity with { OperationId = Guid.NewGuid() } };
        Assert.Equal(OperationOutcomeKind.Conflict, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        fixture.Request = originalRequest with { Identity = originalRequest.Identity with { Resource = originalRequest.Identity.Resource with { ProfileFingerprint = new string('a', 64) } } };
        Assert.Equal(OperationOutcomeKind.Conflict, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(1, fixture.ApplyCount);
    }

    [Fact]
    public async Task JournalFailureNeverAllowsAnUnrecordedApply()
    {
        var fixture = new OperationFixture();
        fixture.Journal.FailRecord = true;
        var result = await fixture.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcomeKind.Unknown, result.Kind);
        Assert.Equal(OperationErrorCode.JournalUnavailable, result.Code);
        Assert.Equal(0, fixture.ApplyCount);
    }

    [Fact]
    public async Task ConfirmationFailureLeavesResultUnknownAndNextPassOnlyObserves()
    {
        var fixture = new OperationFixture();
        fixture.Journal.FailConfirm = true;
        Assert.Equal(OperationOutcomeKind.Unknown, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        fixture.Journal.FailConfirm = false;
        Assert.Equal(OperationOutcomeKind.Applied, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(1, fixture.ApplyCount);
    }

    [Fact]
    public async Task FailedVerificationDoesNotConfirmOrPermitAnotherApply()
    {
        var fixture = new OperationFixture { ApplyChangesState = false };
        Assert.Equal(OperationOutcomeKind.Unknown, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Null(fixture.Journal.Entry!.Confirmation);
        Assert.Equal(OperationOutcomeKind.Unknown, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(1, fixture.ApplyCount);
    }

    [Fact]
    public async Task CancellationBeforeIntentIsDistinctFromCancellationAfterIntent()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var fixture = new OperationFixture();
        Assert.Equal(OperationOutcomeKind.Cancelled, (await fixture.RunAsync(cancellation.Token)).Kind);
        Assert.Empty(fixture.Events);
        using var afterIntent = new CancellationTokenSource();
        fixture.Journal.OnRecorded = () => afterIntent.Cancel();
        Assert.Equal(OperationOutcomeKind.Unknown, (await fixture.RunAsync(afterIntent.Token)).Kind);
        Assert.Equal(0, fixture.ApplyCount);
        Assert.NotNull(fixture.Journal.Entry);
    }

    [Fact]
    public async Task ReadFailureAfterPreviousIntentRemainsUnknown()
    {
        var fixture = new OperationFixture { ReadFailure = OperationResult.Failed(OperationOutcomeKind.Transport, OperationErrorCode.ReadFailed) };
        Assert.Equal(OperationOutcomeKind.Transport, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        fixture.ReadFailure = null;
        fixture.WriteFailure = OperationResult.Failed(OperationOutcomeKind.Unknown, OperationErrorCode.WriteUncertain);
        await fixture.RunAsync(TestContext.Current.CancellationToken);
        fixture.ReadFailure = OperationResult.Failed(OperationOutcomeKind.Transport, OperationErrorCode.ReadFailed);
        Assert.Equal(OperationOutcomeKind.Unknown, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(1, fixture.ApplyCount);
    }

    [Fact]
    public void SuccessAndFailureChannelsRejectContradictoryOutcomes()
    {
        Assert.Throws<ArgumentException>(() => OperationResult.Failed(OperationOutcomeKind.Applied, OperationErrorCode.WriteUncertain));
        Assert.Throws<ArgumentException>(() => OperationResult.Failed(OperationOutcomeKind.Unknown, OperationErrorCode.None));
        Assert.Throws<ArgumentException>(() => OperationRead<OperationObservation>.Failed(OperationResult.Completed(true)));
        Assert.Throws<ArgumentException>(() => OperationWriteAttempt.Failed(OperationResult.Completed(false)));
    }

    [Fact]
    public async Task SharedJournalLeaseBlocksAnotherScenarioAndIsReleased()
    {
        var fixture = new OperationFixture();
        await using (var lease = await fixture.Journal.AcquireAsync(fixture.Descriptor, TestContext.Current.CancellationToken))
        {
            var result = await new ConfigurationOperationScenario(fixture.Journal, fixture).ExecuteAsync(fixture, TestContext.Current.CancellationToken);
            Assert.Equal(OperationErrorCode.OperationInProgress, result.Code);
            Assert.DoesNotContain("observe", fixture.Events);
        }
        Assert.Equal(OperationOutcomeKind.Applied, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
    }

    [Fact]
    public async Task SameScenarioCannotBeEnteredTwiceWhileObservationWaits()
    {
        var fixture = new OperationFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.OnObserve = async () =>
        {
            entered.SetResult();
            await resume.Task.WaitAsync(TestContext.Current.CancellationToken);
        };
        var first = fixture.RunAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(OperationErrorCode.OperationInProgress, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Code);
        }
        finally
        {
            resume.TrySetResult();
        }
        Assert.Equal(OperationOutcomeKind.Applied, (await first).Kind);
    }

    [Fact]
    public async Task OwnershipChangeWhileJournalIsPersistingPreventsApply()
    {
        var fixture = new OperationFixture();
        fixture.Journal.OnRecorded = () => fixture.Ownership = ResourceOwnership.Foreign;
        Assert.Equal(OperationOutcomeKind.Unknown, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Equal(0, fixture.ApplyCount);
        Assert.Null(fixture.Journal.Entry!.Confirmation);
    }

    [Fact]
    public async Task UnreadableJournalDoesNotLeakRawErrorsOrStartApiCalls()
    {
        var fixture = new OperationFixture();
        fixture.Journal.FailRead = true;
        var result = await fixture.RunAsync(TestContext.Current.CancellationToken);
        Assert.Equal(OperationOutcomeKind.Transport, result.Kind);
        Assert.Equal(OperationErrorCode.JournalUnavailable, result.Code);
        Assert.DoesNotContain("synthetic-secret", result.ToString());
        Assert.DoesNotContain("observe", fixture.Events);
        Assert.Equal("unlock", fixture.Events.Last());
    }

    [Fact]
    public async Task InvalidIdentityNeverAcquiresLeaseOrContactsPorts()
    {
        var fixture = new OperationFixture();
        fixture.Request = fixture.Request with { Identity = fixture.Request.Identity with { OperationId = Guid.Empty } };
        Assert.Equal(OperationOutcomeKind.Invalid, (await fixture.RunAsync(TestContext.Current.CancellationToken)).Kind);
        Assert.Empty(fixture.Events);
    }
}

/// <summary>Test-only ownership/journal. Not a production fallback, durable store or NAS proof.</summary>
internal sealed class OperationFixture : IConfigurationOperation, IOperationOwnership
{
    private readonly ConfigurationOperationScenario scenario;

    private int ownershipCount;

    public OperationFixture()
    {
        Journal = new TestOperationJournal(Events);
        scenario = new(Journal, this);
    }

    public static ConfigurationRequest NewRequest(string service = "qbittorrent") => new(
        new(Guid.NewGuid(), new(Guid.NewGuid(), Guid.NewGuid(), service, PinnedRuntimeProfile.Fingerprint, ConfigurationRequest.Fingerprint("http://127.0.0.1:9876/"))),
        service == "qbittorrent" ? ConfigurationOperationKind.QbittorrentCategory : ConfigurationOperationKind.ArrRootFolder,
        service == "qbittorrent" ? "movies" : "root",
        service == "qbittorrent" ? "/data/torrents/movies" : "/data/media/movies"
    );

    public ConfigurationRequest Request { get; set; } = NewRequest();

    public OperationDescriptor Descriptor => Request.Descriptor;

    public List<string> Events { get; } = [];

    public TestOperationJournal Journal { get; }

    public ObservedResourceState State { get; set; }

    public string Revision { get; set; } = new('a', 64);

    public ResourceOwnership Ownership { get; set; } = ResourceOwnership.Owned;

    public Func<int, ResourceOwnership>? OnOwnership { get; set; }

    public bool ApplyChangesState { get; set; } = true;

    public int ApplyCount { get; private set; }

    public OperationResult? WriteFailure { get; set; }

    public OperationResult? ReadFailure { get; set; }

    public Func<Task>? OnObserve { get; set; }

    public Task<OperationResult> RunAsync(CancellationToken token = default) => scenario.ExecuteAsync(this, token);

    public void ResetOwnershipCount() => ownershipCount = 0;

    public Task<ResourceOwnership> CheckAsync(ResourceIdentity resource, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Events.Add("ownership");
        return Task.FromResult(OnOwnership?.Invoke(++ownershipCount) ?? Ownership);
    }

    public async Task<OperationRead<OperationObservation>> ObserveAsync(CancellationToken token)
    {
        Events.Add("observe");
        if (OnObserve is not null)
            await OnObserve();
        return ReadFailure is null
            ? OperationRead<OperationObservation>.Observed(new(Descriptor, State, Revision))
            : OperationRead<OperationObservation>.Failed(ReadFailure);
    }

    public Task<OperationWriteAttempt> ApplyAsync(OperationObservation baseline, CancellationToken token)
    {
        Events.Add("apply");
        ApplyCount++;
        if (ApplyChangesState)
            State = ObservedResourceState.Satisfied;
        return Task.FromResult(WriteFailure is null ? OperationWriteAttempt.Acknowledged() : OperationWriteAttempt.Failed(WriteFailure));
    }

    public Task<OperationRead<OperationVerification>> VerifyAsync(CancellationToken token)
    {
        Events.Add("verify");
        return Task.FromResult(OperationRead<OperationVerification>.Observed(new(Descriptor, State, Revision)));
    }
}

internal sealed class TestOperationJournal(List<string> events) : IOperationJournal
{
    private bool locked;

    public OperationJournalEntry? Entry { get; set; }

    public bool FailRecord { get; set; }

    public bool FailConfirm { get; set; }

    public bool FailRead { get; set; }

    public Action? OnRecorded { get; set; }

    public Task<IAsyncDisposable> AcquireAsync(OperationDescriptor descriptor, CancellationToken token)
    {
        if (locked)
            throw new OperationLeaseUnavailableException();
        locked = true;
        events.Add("lock");
        return Task.FromResult<IAsyncDisposable>(new TestLease(() =>
        {
            locked = false;
            events.Add("unlock");
        }));
    }

    public Task<OperationJournalEntry?> ReadAsync(OperationDescriptor descriptor, CancellationToken token)
    {
        events.Add("read-journal");
        if (FailRead)
            throw new IOException("synthetic-secret-must-not-escape");
        return Task.FromResult(Entry);
    }

    public Task RecordIntentAsync(OperationIntent intent, CancellationToken token)
    {
        events.Add("intent");
        if (FailRecord || Entry is not null)
            throw new IOException("synthetic-secret-must-not-escape");
        Entry = new(intent);
        OnRecorded?.Invoke();
        return Task.CompletedTask;
    }

    public Task ConfirmAsync(OperationIntent intent, OperationVerification verification, CancellationToken token)
    {
        events.Add("confirm");
        if (FailConfirm)
            throw new IOException("synthetic-secret-must-not-escape");
        Entry = new(intent, verification);
        return Task.CompletedTask;
    }

    private sealed class TestLease(Action release) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            release();
            return ValueTask.CompletedTask;
        }
    }
}
