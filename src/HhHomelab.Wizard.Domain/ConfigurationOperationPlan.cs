namespace HhHomelab.Wizard.Domain;

/// <summary>One immutable step. Dependencies and budget are part of the approved plan.</summary>
public sealed class ConfigurationPlanStep
{
    public ConfigurationPlanStep(OperationDescriptor descriptor, IReadOnlyList<Guid> dependencies, TimeSpan timeout)
    {
        if (descriptor is not { IsValid: true } || dependencies is null
            || dependencies.Count > ConfigurationOperationPlan.MaximumSteps
            || timeout <= TimeSpan.Zero || timeout > TimeSpan.FromHours(1))
            throw new ArgumentException("plan.step.invalid");
        var copy = dependencies.ToArray();
        if (copy.Any(id => id == Guid.Empty || id == descriptor.Identity.OperationId)
            || copy.Distinct().Count() != copy.Length)
            throw new ArgumentException("plan.dependencies.invalid");
        Descriptor = descriptor;
        Dependencies = Array.AsReadOnly(copy);
        Timeout = timeout;
    }

    public OperationDescriptor Descriptor { get; }

    public IReadOnlyList<Guid> Dependencies { get; }

    public TimeSpan Timeout { get; }
}

/// <summary>Bounded, immutable graph for one installation/profile. All steps are mandatory.
/// Construction validates the entire graph before any journal or external operation is touched.</summary>
public sealed class ConfigurationOperationPlan
{
    public const int MaximumSteps = 1024;

    public const int MaximumDependencyEdges = 4096;

    public ConfigurationOperationPlan(IReadOnlyList<ConfigurationPlanStep> steps)
    {
        if (steps is null || steps.Count is < 1 or > MaximumSteps || steps.Any(step => step is null))
            throw new ArgumentException("plan.steps.invalid");
        var copy = steps.ToArray();
        if (copy.Sum(step => (long)step.Dependencies.Count) > MaximumDependencyEdges)
            throw new ArgumentException("plan.dependencies.capacity");
        var byId = new Dictionary<Guid, ConfigurationPlanStep>();
        var resources = new Dictionary<Guid, ResourceIdentity>();
        var scope = copy[0].Descriptor.Identity.Resource;
        foreach (var step in copy)
        {
            var resource = step.Descriptor.Identity.Resource;
            if (resource.InstallationId != scope.InstallationId || resource.ProfileFingerprint != scope.ProfileFingerprint
                || !byId.TryAdd(step.Descriptor.Identity.OperationId, step))
                throw new ArgumentException("plan.identity.invalid");
            if (resources.TryGetValue(resource.ResourceId, out var previous) && previous != resource)
                throw new ArgumentException("plan.identity.invalid");
            resources[resource.ResourceId] = resource;
        }
        if (copy.Any(step => step.Dependencies.Any(id => !byId.ContainsKey(id))))
            throw new ArgumentException("plan.dependency.missing");
        Steps = Array.AsReadOnly(copy);
        ExecutionOrder = Array.AsReadOnly(Sort(copy));
        Fingerprint = ConfigurationRequest.Fingerprint(copy.Select(step => new
        {
            step.Descriptor,
            Dependencies = step.Dependencies.Order().ToArray(),
            TimeoutTicks = step.Timeout.Ticks,
        }).ToArray());
    }

    public IReadOnlyList<ConfigurationPlanStep> Steps { get; }

    public IReadOnlyList<ConfigurationPlanStep> ExecutionOrder { get; }

    public string Fingerprint { get; }

    private static ConfigurationPlanStep[] Sort(ConfigurationPlanStep[] steps)
    {
        var ordered = new List<ConfigurationPlanStep>(steps.Length);
        var completed = new HashSet<Guid>();
        while (ordered.Count < steps.Length)
        {
            var progress = false;
            foreach (var step in steps)
            {
                var id = step.Descriptor.Identity.OperationId;
                if (completed.Contains(id) || step.Dependencies.Any(dependency => !completed.Contains(dependency)))
                    continue;
                ordered.Add(step);
                completed.Add(id);
                progress = true;
            }
            if (!progress)
                throw new ArgumentException("plan.dependencies.cycle");
        }
        return ordered.ToArray();
    }
}
