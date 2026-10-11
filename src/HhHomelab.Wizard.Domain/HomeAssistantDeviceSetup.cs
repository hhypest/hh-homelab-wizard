namespace HhHomelab.Wizard.Domain;

public enum HomeAssistantDeviceIntegration { SynologyDsm, LgWebOs, AndroidTvRemote, GoogleCast }
public enum HomeAssistantDeviceSetupStage { AwaitingInput, AwaitingSetup, EntryLoaded, OutcomeUnknown, Aborted }
public enum HomeAssistantDeviceInput { None, Host, DsmCredentials, DsmOtp, DsmBackupLocation, LgConfirmation, AndroidPin, CastHosts }
public enum HomeAssistantDeviceProblem { None, InvalidAuth, CannotConnect, PairingRejected, OtpRejected, MissingData, AlreadyConfigured, AlreadyInProgress }

/// <summary>Explicit host for DSM/TV; Cast is a global integration and has no single target host.</summary>
public sealed record HomeAssistantDeviceTarget(HomeAssistantDeviceIntegration Integration, string? Host);

/// <summary>Safe progress only. EntryLoaded does not prove physical identity, measurements or control.
/// BackupShares contains technical selector values, never credentials, defaults or response bodies.</summary>
public sealed record HomeAssistantDeviceProgress(HomeAssistantDeviceSetupStage Stage, HomeAssistantDeviceInput Input,
    HomeAssistantDeviceProblem Problem, string? EntryId, IReadOnlyList<string> BackupShares);
