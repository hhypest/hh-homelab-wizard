namespace HhHomelab.Wizard.Domain;

public enum WizardState
{
    Running,
    WaitingForUser,
    Succeeded,
    Failed,
    Cancelled,
    OutcomeUnknown,
}

public enum WizardOperation
{
    PrepareFolders,
    ConfigureMedia,
    ConfigureHomeAssistant,
    PairDevice,
    ConfigureDashboard,
    Verify,
}

public sealed record WizardProblem(string Code, string Message, string Diagnostic);

public sealed record WizardProgress(
    WizardState State,
    WizardOperation Operation,
    int Completed,
    int Total,
    WizardProblem? Problem = null
);
