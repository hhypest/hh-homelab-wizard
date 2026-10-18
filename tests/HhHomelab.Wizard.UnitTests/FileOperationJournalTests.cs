using System.Text.Json.Nodes;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class FileOperationJournalTests
{
    public static bool RestrictedSymlinkCreation => OperatingSystem.IsWindows();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IntentAndConfirmationSurviveReopeningWithExclusiveLease()
    {
        using var fixture = new JournalDirectory();
        var journal = fixture.Create();
        await using (await journal.AcquireAsync(fixture.Descriptor, Token))
        {
            Assert.Null(await journal.ReadAsync(fixture.Descriptor, Token));
            await journal.RecordIntentAsync(fixture.Intent, Token);
            Assert.Equal(new OperationJournalEntry(fixture.Intent), await journal.ReadAsync(fixture.Descriptor, Token));
        }
        var reopened = fixture.Open();
        await using (await reopened.AcquireAsync(fixture.Descriptor, Token))
        {
            Assert.Equal(fixture.Intent, (await reopened.ReadAsync(fixture.Descriptor, Token))!.Intent);
            await reopened.ConfirmAsync(fixture.Intent, fixture.Verification, Token);
        }
        var confirmed = fixture.Open();
        await using (await confirmed.AcquireAsync(fixture.Descriptor, Token))
            Assert.Equal(fixture.Verification, (await confirmed.ReadAsync(fixture.Descriptor, Token))!.Confirmation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingIntentUsesObservationOnlyAfterReopen(bool present)
    {
        using var fixture = new JournalDirectory();
        var journal = fixture.Create();
        await using (await journal.AcquireAsync(fixture.Descriptor, Token))
            await journal.RecordIntentAsync(fixture.Intent, Token);
        var operation = new OperationFixture
        {
            Request = fixture.Request,
            State = present ? ObservedResourceState.Satisfied : ObservedResourceState.Missing,
        };
        var result = await new ConfigurationOperationScenario(fixture.Open(), operation).ExecuteAsync(operation, Token);
        Assert.Equal(present ? OperationOutcomeKind.Applied : OperationOutcomeKind.Unknown, result.Kind);
        Assert.Equal(0, operation.ApplyCount);
    }

    [Fact]
    public async Task ConfirmedIntentIsNotAppliedAgainAfterReopen()
    {
        using var fixture = new JournalDirectory();
        var operation = new OperationFixture { Request = fixture.Request };
        Assert.Equal(OperationOutcomeKind.Applied,
            (await new ConfigurationOperationScenario(fixture.Create(), operation).ExecuteAsync(operation, Token)).Kind);
        Assert.Equal(OperationOutcomeKind.AlreadySatisfied,
            (await new ConfigurationOperationScenario(fixture.Open(), operation).ExecuteAsync(operation, Token)).Kind);
        Assert.Equal(1, operation.ApplyCount);
    }

    [Fact]
    public async Task StorageDisappearingBeforeIntentBlocksTheExternalMutation()
    {
        using var fixture = new JournalDirectory();
        var journal = fixture.Create();
        var operation = new OperationFixture
        {
            Request = fixture.Request,
            OnObserve = () =>
            {
                File.Delete(fixture.SnapshotPath);
                return Task.CompletedTask;
            },
        };
        var result = await new ConfigurationOperationScenario(journal, operation).ExecuteAsync(operation, Token);
        Assert.Equal(OperationOutcomeKind.Unknown, result.Kind);
        Assert.Equal(OperationErrorCode.JournalUnavailable, result.Code);
        Assert.Equal(0, operation.ApplyCount);
    }

    [Fact]
    public async Task InstallationLockCoversDifferentResourcesAndRejectsSecondInstance()
    {
        using var fixture = new JournalDirectory();
        var first = fixture.Create();
        var second = fixture.Open();
        var descriptor = fixture.Descriptor with
        {
            Identity = fixture.Descriptor.Identity with
            {
                Resource = fixture.Descriptor.Identity.Resource with { ResourceId = Guid.NewGuid() },
            },
        };
        var lease = await first.AcquireAsync(fixture.Descriptor, Token);
        await Assert.ThrowsAsync<OperationLeaseUnavailableException>(() => second.AcquireAsync(descriptor, Token));
        await Assert.ThrowsAsync<OperationLeaseUnavailableException>(() => first.AcquireAsync(fixture.Descriptor, Token));
        await lease.DisposeAsync();
        await using var next = await second.AcquireAsync(descriptor, Token);
        await lease.DisposeAsync();
        Assert.Null(await second.ReadAsync(descriptor, Token));
    }

    [Fact]
    public async Task PendingIntentCannotBeHiddenByAnotherOperationId()
    {
        using var fixture = new JournalDirectory();
        var journal = fixture.Create();
        await using (await journal.AcquireAsync(fixture.Descriptor, Token))
            await journal.RecordIntentAsync(fixture.Intent, Token);
        var different = fixture.Descriptor with
        {
            Identity = fixture.Descriptor.Identity with { OperationId = Guid.NewGuid() },
        };
        var error = await Assert.ThrowsAsync<OperationJournalException>(() => journal.AcquireAsync(different, Token));
        Assert.Equal("journal.pending-conflict", error.Code);
    }

    [Theory]
    [InlineData("specification")]
    [InlineData("endpoint")]
    [InlineData("resource")]
    [InlineData("profile")]
    [InlineData("installation")]
    public async Task ChangedDescriptorNeverResumesTheOldOperation(string field)
    {
        using var fixture = new JournalDirectory();
        var journal = fixture.Create();
        await using (await journal.AcquireAsync(fixture.Descriptor, Token))
            await journal.RecordIntentAsync(fixture.Intent, Token);
        var descriptor = fixture.Descriptor;
        var resource = descriptor.Identity.Resource;
        descriptor = field switch
        {
            "specification" => descriptor with { SpecificationHash = new string('b', 64) },
            "endpoint" => descriptor with { Identity = descriptor.Identity with { Resource = resource with { EndpointFingerprint = new string('b', 64) } } },
            "resource" => descriptor with { Identity = descriptor.Identity with { Resource = resource with { ResourceId = Guid.NewGuid() } } },
            "profile" => descriptor with { Identity = descriptor.Identity with { Resource = resource with { ProfileFingerprint = new string('b', 64) } } },
            _ => descriptor with { Identity = descriptor.Identity with { Resource = resource with { InstallationId = Guid.NewGuid() } } },
        };
        await Assert.ThrowsAsync<OperationJournalException>(() => journal.AcquireAsync(descriptor, Token));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("installation")]
    [InlineData("profile")]
    public void ForeignScopeCannotOpenExistingJournal(string field)
    {
        using var fixture = new JournalDirectory();
        fixture.Create();
        var scope = field switch
        {
            "session" => fixture.Scope with { SessionId = Guid.NewGuid() },
            "installation" => fixture.Scope with { InstallationId = Guid.NewGuid() },
            _ => fixture.Scope with { ProfileFingerprint = new string('b', 64) },
        };
        Assert.Equal("journal.identity", Assert.Throws<OperationJournalException>(() => FileOperationJournal.Open(fixture.Path, scope)).Code);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("hash")]
    [InlineData("schema")]
    [InlineData("unknown")]
    [InlineData("empty")]
    [InlineData("oversized")]
    public void CorruptOrIncompatibleJournalDoesNotBecomeANewInstallation(string failure)
    {
        using var fixture = new JournalDirectory();
        fixture.Create();
        var node = JsonNode.Parse(File.ReadAllText(fixture.SnapshotPath))!;
        switch (failure)
        {
            case "truncated":
                File.WriteAllText(fixture.SnapshotPath, "{\"synthetic-token\"");
                break;
            case "empty":
                File.WriteAllText(fixture.SnapshotPath, "");
                break;
            case "oversized":
                File.WriteAllBytes(fixture.SnapshotPath, new byte[4 * 1024 * 1024 + 1]);
                break;
            default:
                if (failure == "hash")
                    node["Hash"] = new string('0', 64);
                if (failure == "schema")
                    node["Data"]!["SchemaVersion"] = 2;
                if (failure == "unknown")
                    node["password"] = "synthetic-token";
                File.WriteAllText(fixture.SnapshotPath, node.ToJsonString());
                break;
        }
        var error = Assert.Throws<OperationJournalException>(() => fixture.Open());
        Assert.DoesNotContain("synthetic-token", error.ToString());
        Assert.Null(error.InnerException);
        Assert.Throws<OperationJournalException>(() => fixture.Create());
    }

    [Theory]
    [InlineData("journal.json")]
    [InlineData("journal.lock")]
    public void MissingJournalOrLockIsNotRecreated(string file)
    {
        using var fixture = new JournalDirectory();
        fixture.Create();
        File.Delete(System.IO.Path.Combine(fixture.Path, file));
        Assert.Throws<OperationJournalException>(() => fixture.Open());
        Assert.False(File.Exists(System.IO.Path.Combine(fixture.Path, file)));
    }

    [Fact]
    public async Task EveryMethodRequiresItsOwnActiveDescriptorLease()
    {
        using var fixture = new JournalDirectory();
        var journal = fixture.Create();
        await Assert.ThrowsAsync<OperationJournalException>(() => journal.ReadAsync(fixture.Descriptor, Token));
        await Assert.ThrowsAsync<OperationJournalException>(() => journal.RecordIntentAsync(fixture.Intent, Token));
        await Assert.ThrowsAsync<OperationJournalException>(() => journal.ConfirmAsync(fixture.Intent, fixture.Verification, Token));
        var lease = await journal.AcquireAsync(fixture.Descriptor, Token);
        var other = fixture.Descriptor with { SpecificationHash = new string('b', 64) };
        await Assert.ThrowsAsync<OperationJournalException>(() => journal.ReadAsync(other, Token));
        await lease.DisposeAsync();
        await Assert.ThrowsAsync<OperationJournalException>(() => journal.ReadAsync(fixture.Descriptor, Token));
    }

    [Fact]
    public async Task ConfirmationNeedsExactIntentAndIndependentSatisfiedEvidence()
    {
        using var fixture = new JournalDirectory();
        var journal = fixture.Create();
        await using var lease = await journal.AcquireAsync(fixture.Descriptor, Token);
        await Assert.ThrowsAsync<OperationJournalException>(() => journal.ConfirmAsync(fixture.Intent, fixture.Verification, Token));
        await journal.RecordIntentAsync(fixture.Intent, Token);
        var changedIntent = fixture.Intent with { Baseline = fixture.Intent.Baseline with { Revision = new string('b', 64) } };
        await Assert.ThrowsAsync<OperationJournalException>(() => journal.ConfirmAsync(changedIntent, fixture.Verification, Token));
        var missing = fixture.Verification with { State = ObservedResourceState.Missing };
        await Assert.ThrowsAsync<OperationJournalException>(() => journal.ConfirmAsync(fixture.Intent, missing, Token));
        Assert.Null((await journal.ReadAsync(fixture.Descriptor, Token))!.Confirmation);
    }

    [Fact]
    public async Task CancellationDoesNotEraseAnIntentOrPublishAConfirmation()
    {
        using var fixture = new JournalDirectory();
        var journal = fixture.Create();
        await using var lease = await journal.AcquireAsync(fixture.Descriptor, Token);
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.RecordIntentAsync(fixture.Intent, cancelled.Token));
        Assert.Null(await journal.ReadAsync(fixture.Descriptor, Token));
        await journal.RecordIntentAsync(fixture.Intent, Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.ConfirmAsync(fixture.Intent, fixture.Verification, cancelled.Token));
        Assert.Equal(new OperationJournalEntry(fixture.Intent), await journal.ReadAsync(fixture.Descriptor, Token));
    }

    [Fact]
    public async Task ReplacingTheSnapshotDuringALeaseBlocksTheNextWrite()
    {
        using var fixture = new JournalDirectory();
        var journal = fixture.Create();
        var oldSnapshot = File.ReadAllBytes(fixture.SnapshotPath);
        await using var lease = await journal.AcquireAsync(fixture.Descriptor, Token);
        await journal.RecordIntentAsync(fixture.Intent, Token);
        File.WriteAllBytes(fixture.SnapshotPath, oldSnapshot);
        var error = await Assert.ThrowsAsync<OperationJournalException>(() => journal.ConfirmAsync(fixture.Intent, fixture.Verification, Token));
        Assert.Equal("journal.changed", error.Code);
    }

    [Fact]
    public void OrphanTemporaryFilesAreNeverPromoted()
    {
        using var fixture = new JournalDirectory();
        fixture.Create();
        File.Move(fixture.SnapshotPath, System.IO.Path.Combine(fixture.Path, "journal-orphan.tmp"));
        Assert.Throws<OperationJournalException>(() => fixture.Open());
    }

    [Theory]
    [InlineData("duplicate-operation")]
    [InlineData("baseline")]
    [InlineData("confirmation")]
    [InlineData("resource")]
    public async Task AValidChecksumDoesNotAuthorizeInvalidJournalSemantics(string failure)
    {
        using var fixture = new JournalDirectory();
        var journal = fixture.Create();
        await using (await journal.AcquireAsync(fixture.Descriptor, Token))
            await journal.RecordIntentAsync(fixture.Intent, Token);
        var node = JsonNode.Parse(File.ReadAllText(fixture.SnapshotPath))!;
        var data = node["Data"]!;
        var entries = data["Entries"]!.AsArray();
        switch (failure)
        {
            case "duplicate-operation":
                entries.Add(entries[0]!.DeepClone());
                break;
            case "baseline":
                entries[0]!["Intent"]!["Baseline"]!["State"] = (int)ObservedResourceState.Satisfied;
                break;
            case "confirmation":
                entries[0]!["Confirmation"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(fixture.Verification with { State = ObservedResourceState.Conflict }));
                break;
            case "resource":
                entries[0]!["Intent"]!["Descriptor"]!["Identity"]!["Resource"]!["InstallationId"] = Guid.NewGuid();
                break;
        }
        node["Hash"] = ConfigurationRequest.Fingerprint(data);
        File.WriteAllText(fixture.SnapshotPath, node.ToJsonString());
        var expected = failure switch
        {
            "duplicate-operation" => "journal.duplicate",
            "baseline" => "journal.baseline",
            "confirmation" => "journal.invalid",
            _ => "journal.identity",
        };
        Assert.Equal(expected, Assert.Throws<OperationJournalException>(() => fixture.Open()).Code);
    }

    [Fact]
    public async Task ConfirmedHistorySupportsAnotherApprovedOperationWithoutHidingItsPendingIntent()
    {
        using var fixture = new JournalDirectory();
        var journal = fixture.Create();
        await using (await journal.AcquireAsync(fixture.Descriptor, Token))
        {
            await journal.RecordIntentAsync(fixture.Intent, Token);
            await journal.ConfirmAsync(fixture.Intent, fixture.Verification, Token);
            await Assert.ThrowsAsync<OperationJournalException>(() => journal.RecordIntentAsync(fixture.Intent, Token));
        }
        var next = fixture.Descriptor with { Identity = fixture.Descriptor.Identity with { OperationId = Guid.NewGuid() } };
        var nextIntent = new OperationIntent(next, fixture.Intent.Baseline with { Descriptor = next });
        await using (await journal.AcquireAsync(next, Token))
        {
            Assert.Null(await journal.ReadAsync(next, Token));
            await journal.RecordIntentAsync(nextIntent, Token);
        }
        await Assert.ThrowsAsync<OperationJournalException>(() => journal.AcquireAsync(fixture.Descriptor, Token));
    }

    [Fact(Skip = "Unprivileged Windows symlink creation is not assumed.", SkipWhen = nameof(RestrictedSymlinkCreation))]
    public void LinkedDirectoryOrSnapshotCannotRedirectTheJournal()
    {
        using var fixture = new JournalDirectory();
        fixture.Create();
        var link = System.IO.Path.Combine(fixture.Path, "linked");
        Directory.CreateSymbolicLink(link, fixture.Path);
        try
        {
            Assert.Throws<OperationJournalException>(() => FileOperationJournal.Open(link, fixture.Scope));
        }
        finally
        {
            Directory.Delete(link);
        }
        File.Move(fixture.SnapshotPath, System.IO.Path.Combine(fixture.Path, "original.json"));
        File.CreateSymbolicLink(fixture.SnapshotPath, System.IO.Path.Combine(fixture.Path, "original.json"));
        Assert.Equal("journal.link", Assert.Throws<OperationJournalException>(() => fixture.Open()).Code);
        File.Delete(fixture.SnapshotPath);
    }
}

internal sealed class JournalDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wizard-journal-test-{Guid.NewGuid():N}");

    public string SnapshotPath => System.IO.Path.Combine(Path, "journal.json");

    public ConfigurationRequest Request { get; } = OperationFixture.NewRequest("qbittorrent");

    public OperationJournalScope Scope { get; }

    public OperationDescriptor Descriptor => Request.Descriptor;

    public OperationIntent Intent => new(Descriptor, new(Descriptor, ObservedResourceState.Missing, new string('a', 64)));

    public OperationVerification Verification => new(Descriptor, ObservedResourceState.Satisfied, new string('b', 64));

    public JournalDirectory()
    {
        Directory.CreateDirectory(Path);
        Scope = new(Request.Identity.Resource.InstallationId, Guid.NewGuid(), Request.Identity.Resource.ProfileFingerprint);
    }

    public FileOperationJournal Create() => FileOperationJournal.Create(Path, Scope);

    public FileOperationJournal Open() => FileOperationJournal.Open(Path, Scope);

    public void Dispose()
    {
        var parent = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()));
        if (System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path)) != parent)
            throw new InvalidOperationException("test.directory.invalid");
        Directory.Delete(Path, recursive: true);
    }
}
