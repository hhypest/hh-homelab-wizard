using System.Text.Json.Nodes;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;
using HhHomelab.Wizard.Infrastructure;
using Xunit;

namespace HhHomelab.Wizard.UnitTests;

public sealed class ConfigurationPlanTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DependenciesExecuteBeforeTheirConsumersAndEverySuccessIsReobservedOnResume()
    {
        using var fixture = new PlanFixture();
        var first = await fixture.RunAsync(Token);
        Assert.Equal(OperationOutcomeKind.Applied, first.Result.Kind);
        Assert.Equal(new[] { "first.observe", "first.apply", "first.verify", "second.observe", "second.apply", "second.verify" }, fixture.Events);
        fixture.Events.Clear();
        var resumed = await fixture.RunAsync(Token);
        Assert.Equal(OperationOutcomeKind.AlreadySatisfied, resumed.Result.Kind);
        Assert.Equal(new[] { "first.observe", "first.verify", "second.observe", "second.verify" }, fixture.Events);
        Assert.Equal(1, fixture.First.ApplyCount);
        Assert.Equal(1, fixture.Second.ApplyCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostResponseStopsDependentsAndReopenedJournalNeverBlindlyRepeatsApply(bool published)
    {
        using var fixture = new PlanFixture();
        fixture.First.LoseResponse = true;
        fixture.First.Publish = published;
        var first = await fixture.RunAsync(Token);
        Assert.Equal(OperationOutcomeKind.Unknown, first.Result.Kind);
        Assert.Single(first.Steps);
        Assert.Equal(0, fixture.Second.ObserveCount);
        fixture.First.LoseResponse = false;
        var resumed = await fixture.RunAsync(Token);
        Assert.Equal(published ? OperationOutcomeKind.Applied : OperationOutcomeKind.Unknown, resumed.Result.Kind);
        Assert.Equal(1, fixture.First.ApplyCount);
        Assert.Equal(published ? 1 : 0, fixture.Second.ApplyCount);
    }

    [Fact]
    public async Task APreviouslyConfirmedDependencyThatDisappearsBlocksTheRemainingPlan()
    {
        using var fixture = new PlanFixture();
        Assert.True((await fixture.RunAsync(Token)).Result.IsSuccess);
        fixture.First.State = ObservedResourceState.Missing;
        fixture.Events.Clear();
        var result = await fixture.RunAsync(Token);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Result.Kind);
        Assert.Equal(OperationErrorCode.ObservationChanged, result.Result.Code);
        Assert.Equal(new[] { "first.observe" }, fixture.Events);
        Assert.Equal(1, fixture.First.ApplyCount);
        Assert.Equal(1, fixture.Second.ApplyCount);
    }

    [Fact]
    public async Task LostOwnershipOnResumeNeverUsesStoredSuccessToStartTheNextStep()
    {
        using var fixture = new PlanFixture();
        Assert.True((await fixture.RunAsync(Token)).Result.IsSuccess);
        fixture.Ownership = ResourceOwnership.Foreign;
        fixture.Events.Clear();
        var result = await fixture.RunAsync(Token);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Result.Kind);
        Assert.Equal(OperationErrorCode.OwnershipNotProven, result.Result.Code);
        Assert.Empty(fixture.Events);
    }

    [Theory]
    [InlineData("dependencies")]
    [InlineData("timeout")]
    [InlineData("specification")]
    [InlineData("endpoint")]
    [InlineData("omitted-step")]
    [InlineData("added-step")]
    public async Task AChangedPlanIsRejectedBeforeAnyObservationEvenWhenNoIntentWasWritten(string change)
    {
        using var fixture = new PlanFixture();
        await fixture.Open().BindPlanAsync(fixture.Plan, Token);
        var steps = fixture.Plan.Steps.ToArray();
        var step = steps[0];
        steps[0] = change switch
        {
            "dependencies" => new(step.Descriptor, [], step.Timeout),
            "timeout" => new(step.Descriptor, step.Dependencies, TimeSpan.FromSeconds(29)),
            "specification" => new(step.Descriptor with { SpecificationHash = new string('b', 64) }, step.Dependencies, step.Timeout),
            "endpoint" => new(step.Descriptor with
            {
                Identity = step.Descriptor.Identity with
                {
                    Resource = step.Descriptor.Identity.Resource with { EndpointFingerprint = new string('b', 64) },
                },
            }, step.Dependencies, step.Timeout),
            _ => step,
        };
        if (change == "omitted-step")
            steps = [steps[1]];
        if (change == "added-step")
            steps = [.. steps, new(fixture.NewDescriptor(), [], TimeSpan.FromSeconds(30))];
        var plan = new ConfigurationOperationPlan(steps);
        var operations = plan.Steps.Select((item, index) => (IConfigurationOperation)new PlanOperation(item.Descriptor, $"new{index}", fixture.Events)).ToArray();
        var result = await new ConfigurationPlanScenario(fixture.Open(), fixture).ExecuteAsync(plan, operations, Token);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Result.Kind);
        Assert.Equal(OperationErrorCode.IdentityMismatch, result.Result.Code);
        Assert.Empty(result.Steps);
        Assert.Empty(fixture.Events);
        await fixture.Open().BindPlanAsync(fixture.Plan, Token);
    }

    [Fact]
    public async Task MissingOrMismatchedAdapterPreventsAllExecutionAndPlanBinding()
    {
        using var fixture = new PlanFixture();
        var before = File.ReadAllBytes(fixture.SnapshotPath);
        var scenario = new ConfigurationPlanScenario(fixture.Open(), fixture);
        var result = await scenario.ExecuteAsync(fixture.Plan, [fixture.First], Token);
        Assert.Equal(OperationOutcomeKind.Conflict, result.Result.Kind);
        Assert.Equal(before, File.ReadAllBytes(fixture.SnapshotPath));
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task BoundPlanRejectsAnUnplannedOperationThroughTheSingleOperationScenario()
    {
        using var fixture = new PlanFixture();
        var journal = fixture.Open();
        await journal.BindPlanAsync(fixture.Plan, Token);
        var extra = new PlanOperation(fixture.NewDescriptor(), "extra", fixture.Events);
        var result = await new ConfigurationOperationScenario(journal, fixture).ExecuteAsync(extra, Token);
        Assert.False(result.IsSuccess);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task ExistingSingleOperationHistoryCannotBeAdoptedIntoANewPlan()
    {
        using var fixture = new PlanFixture();
        var journal = fixture.Open();
        var descriptor = fixture.First.Descriptor;
        await using (await journal.AcquireAsync(descriptor, Token))
            await journal.RecordIntentAsync(new(descriptor, new(descriptor, ObservedResourceState.Missing, new string('a', 64))), Token);
        var result = await fixture.RunAsync(Token);
        Assert.False(result.Result.IsSuccess);
        Assert.Empty(fixture.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeadlineBeforeIntentIsTransportButAfterSendingRemainsUnknown(bool afterSend)
    {
        using var fixture = new PlanFixture(TimeSpan.FromMilliseconds(500));
        fixture.First.WaitAt = afterSend ? "apply" : "observe";
        var result = await fixture.RunAsync(Token);
        Assert.Equal(afterSend ? OperationOutcomeKind.Unknown : OperationOutcomeKind.Transport, result.Result.Kind);
        Assert.Equal(afterSend ? OperationErrorCode.WriteUncertain : OperationErrorCode.DeadlineExceeded, result.Result.Code);
        Assert.Single(result.Steps);
        Assert.Equal(0, fixture.Second.ObserveCount);
        fixture.First.WaitAt = null;
        if (afterSend)
        {
            var resumed = await fixture.RunAsync(Token);
            Assert.Equal(OperationOutcomeKind.Unknown, resumed.Result.Kind);
            Assert.Equal(1, fixture.First.ApplyCount);
        }
    }

    [Fact]
    public async Task CancellationBetweenStepsPreservesEarlierSuccessAndDoesNotStartTheNextStep()
    {
        using var fixture = new PlanFixture();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        fixture.First.State = ObservedResourceState.Satisfied;
        fixture.First.AfterVerify = cancelled.Cancel;
        var result = await fixture.RunAsync(cancelled.Token);
        Assert.Equal(OperationOutcomeKind.Cancelled, result.Result.Kind);
        Assert.Single(result.Steps);
        Assert.True(result.Steps[0].Result.IsSuccess);
        Assert.Equal(0, fixture.Second.ObserveCount);
    }

    [Fact]
    public async Task ConcurrentRunIsRejectedAndPlanLeaseConflictsDoNotExecuteSteps()
    {
        using var fixture = new PlanFixture();
        var journal = fixture.Open();
        var scenario = new ConfigurationPlanScenario(journal, fixture);
        var otherScenario = new ConfigurationPlanScenario(fixture.Open(), fixture);
        fixture.First.WaitAt = "observe";
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var first = scenario.ExecuteAsync(fixture.Plan, fixture.Operations, cancelled.Token);
        await fixture.First.Entered.Task.WaitAsync(Token);
        var second = await scenario.ExecuteAsync(fixture.Plan, fixture.Operations, Token);
        Assert.Equal(OperationErrorCode.OperationInProgress, second.Result.Code);
        var other = await otherScenario.ExecuteAsync(fixture.Plan, fixture.Operations, Token);
        Assert.Equal(OperationErrorCode.OperationInProgress, other.Result.Code);
        cancelled.Cancel();
        Assert.Equal(OperationOutcomeKind.Cancelled, (await first).Result.Kind);
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    public void InvalidGraphsAreRejectedBeforeExecution(string kind)
    {
        using var fixture = new PlanFixture();
        var first = fixture.First.Descriptor;
        var second = fixture.Second.Descriptor;
        var budget = TimeSpan.FromSeconds(30);
        var steps = kind switch
        {
            "cycle" => new[] { new ConfigurationPlanStep(first, [second.Identity.OperationId], budget), new(second, [first.Identity.OperationId], budget) },
            "missing" => [new(first, [Guid.NewGuid()], budget)],
            "duplicate" => [new(first, [], budget), new(first, [], budget)],
            _ => [new(first, [], budget), new(second with { Identity = second.Identity with { Resource = second.Identity.Resource with { InstallationId = Guid.NewGuid() } } }, [], budget)],
        };
        Assert.Throws<ArgumentException>(() => new ConfigurationOperationPlan(steps));
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public void CallerCannotChangeTheFrozenGraphThroughItsInputArrays()
    {
        using var fixture = new PlanFixture();
        Guid[] dependencies = [fixture.First.Descriptor.Identity.OperationId];
        ConfigurationPlanStep[] steps = [new(fixture.Second.Descriptor, dependencies, TimeSpan.FromSeconds(30)), fixture.Plan.Steps[1]];
        var plan = new ConfigurationOperationPlan(steps);
        var hash = plan.Fingerprint;
        dependencies[0] = Guid.NewGuid();
        steps[0] = new(fixture.Second.Descriptor, [], TimeSpan.FromSeconds(1));
        Assert.Equal(fixture.First.Descriptor.Identity.OperationId, plan.Steps[0].Dependencies[0]);
        Assert.Equal(hash, plan.Fingerprint);
        Assert.Equal(fixture.First.Descriptor, plan.ExecutionOrder[0].Descriptor);
    }

    [Theory]
    [InlineData("fingerprint")]
    [InlineData("cycle")]
    [InlineData("member")]
    [InlineData("null-step")]
    public async Task AValidOuterChecksumDoesNotAuthorizeInvalidPlanSemantics(string kind)
    {
        using var fixture = new PlanFixture();
        await fixture.Open().BindPlanAsync(fixture.Plan, Token);
        var node = JsonNode.Parse(File.ReadAllText(fixture.SnapshotPath))!;
        var data = node["Data"]!;
        var plan = data["Plan"]!;
        switch (kind)
        {
            case "fingerprint":
                plan["Fingerprint"] = new string('b', 64);
                break;
            case "cycle":
                plan["Steps"]![1]!["Dependencies"] = new JsonArray(fixture.Second.Descriptor.Identity.OperationId.ToString());
                break;
            case "member":
                plan["Steps"]![0]!["Descriptor"]!["Identity"]!["Resource"]!["InstallationId"] = Guid.NewGuid();
                break;
            default:
                plan["Steps"]![0] = null;
                break;
        }
        node["Hash"] = ConfigurationRequest.Fingerprint(data);
        File.WriteAllText(fixture.SnapshotPath, node.ToJsonString());
        Assert.Throws<OperationJournalException>(() => fixture.Open());
    }

    [Fact]
    public void PreviousSchemaIsRejectedWithoutRewritingOrResettingTheJournal()
    {
        using var fixture = new PlanFixture();
        var node = JsonNode.Parse(File.ReadAllText(fixture.SnapshotPath))!;
        node["Data"]!["SchemaVersion"] = 1;
        node["Data"]!.AsObject().Remove("Plan");
        File.WriteAllText(fixture.SnapshotPath, node.ToJsonString());
        var before = File.ReadAllBytes(fixture.SnapshotPath);
        Assert.Throws<OperationJournalException>(() => fixture.Open());
        Assert.Equal(before, File.ReadAllBytes(fixture.SnapshotPath));
    }

    private sealed class PlanFixture : IOperationOwnership, IDisposable
    {
        private readonly JournalDirectory directory = new();

        public PlanFixture(TimeSpan? timeout = null)
        {
            First = new(directory.Descriptor, "first", Events);
            Second = new(NewDescriptor(), "second", Events);
            Plan = new([
                new(Second.Descriptor, [First.Descriptor.Identity.OperationId], TimeSpan.FromSeconds(30)),
                new(First.Descriptor, [], timeout ?? TimeSpan.FromSeconds(30)),
            ]);
            directory.Create();
        }

        public string SnapshotPath => directory.SnapshotPath;
        public List<string> Events { get; } = [];
        public PlanOperation First { get; }
        public PlanOperation Second { get; }
        public ConfigurationOperationPlan Plan { get; }
        public IReadOnlyList<IConfigurationOperation> Operations => new IConfigurationOperation[] { Second, First };
        public FileOperationJournal Open() => directory.Open();
        public OperationDescriptor NewDescriptor() => directory.Descriptor with
        {
            Identity = new(Guid.NewGuid(), directory.Descriptor.Identity.Resource with { ResourceId = Guid.NewGuid() }),
        };
        public Task<ConfigurationPlanResult> RunAsync(CancellationToken token) =>
            new ConfigurationPlanScenario(Open(), this).ExecuteAsync(Plan, Operations, token);
        public ResourceOwnership Ownership { get; set; } = ResourceOwnership.Owned;
        public Task<ResourceOwnership> CheckAsync(ResourceIdentity resource, CancellationToken token) => Task.FromResult(Ownership);
        public void Dispose() => directory.Dispose();
    }

    private sealed class PlanOperation(OperationDescriptor descriptor, string name, List<string> events) : IConfigurationOperation
    {
        public OperationDescriptor Descriptor { get; } = descriptor;
        public ObservedResourceState State { get; set; } = ObservedResourceState.Missing;
        public bool LoseResponse { get; set; }
        public bool Publish { get; set; } = true;
        public string? WaitAt { get; set; }
        public Action? AfterVerify { get; set; }
        public int ObserveCount { get; private set; }
        public int ApplyCount { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OperationRead<OperationObservation>> ObserveAsync(CancellationToken token)
        {
            events.Add($"{name}.observe");
            ObserveCount++;
            await WaitAsync("observe", token);
            return OperationRead<OperationObservation>.Observed(new(Descriptor, State, new string('a', 64)));
        }

        public async Task<OperationWriteAttempt> ApplyAsync(OperationObservation baseline, CancellationToken token)
        {
            events.Add($"{name}.apply");
            ApplyCount++;
            await WaitAsync("apply", token);
            if (Publish)
                State = ObservedResourceState.Satisfied;
            return LoseResponse
                ? OperationWriteAttempt.Failed(OperationResult.Failed(OperationOutcomeKind.Unknown, OperationErrorCode.WriteUncertain))
                : OperationWriteAttempt.Acknowledged();
        }

        public Task<OperationRead<OperationVerification>> VerifyAsync(CancellationToken token)
        {
            events.Add($"{name}.verify");
            AfterVerify?.Invoke();
            return Task.FromResult(OperationRead<OperationVerification>.Observed(new(Descriptor, State, new string('a', 64))));
        }

        private async Task WaitAsync(string point, CancellationToken token)
        {
            if (WaitAt != point)
                return;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
    }
}
