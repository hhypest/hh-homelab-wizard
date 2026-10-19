using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

internal sealed record OperationPlanStepData(OperationDescriptor Descriptor, Guid[] Dependencies, long TimeoutTicks);

/// <summary>The complete secret-free definition is stored so membership and graph validity can be
/// checked on every journal load, including calls made outside the graph executor.</summary>
internal sealed record OperationPlanBinding(string Fingerprint, OperationPlanStepData[] Steps)
{
    internal static OperationPlanBinding From(ConfigurationOperationPlan plan) => new(plan.Fingerprint,
        plan.Steps.Select(step => new OperationPlanStepData(step.Descriptor, step.Dependencies.ToArray(), step.Timeout.Ticks)).ToArray());

    internal void Validate(OperationJournalScope scope)
    {
        if (Steps is null || Steps.Length is < 1 or > ConfigurationOperationPlan.MaximumSteps)
            throw new OperationJournalException("journal.plan-invalid");
        foreach (var step in Steps)
        {
            if (step is null)
                throw new OperationJournalException("journal.plan-invalid");
            OperationJournalSnapshot.ValidateDescriptor(step.Descriptor, scope);
        }
        ConfigurationOperationPlan plan;
        try
        {
            plan = new ConfigurationOperationPlan(Steps.Select(step => new ConfigurationPlanStep(
                step.Descriptor, step.Dependencies, TimeSpan.FromTicks(step.TimeoutTicks))).ToArray());
        }
        catch (ArgumentException)
        {
            throw new OperationJournalException("journal.plan-invalid");
        }
        if (plan.Fingerprint != Fingerprint)
            throw new OperationJournalException("journal.plan-hash");
    }

    internal void RequireMember(OperationDescriptor descriptor)
    {
        if (!Steps.Any(step => step.Descriptor == descriptor))
            throw new OperationJournalException("journal.plan-descriptor");
    }
}
