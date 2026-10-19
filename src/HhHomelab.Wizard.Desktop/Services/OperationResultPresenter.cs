using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Desktop.Services;

public enum OperationUserAction
{
    None,
    Observe,
    ReviewPlan,
    CheckConnection,
    CheckJournal,
    SignIn,
    CheckCompatibility,
    CorrectInput,
}

public sealed record OperationPresentation(string Message, OperationUserAction Action);

/// <summary>Presentation of typed operation results, ready for screen VM integration in #103.
/// Never offers an automatic Apply/retry action or displays adapter exception text.</summary>
public static class OperationResultPresenter
{
    public static OperationPresentation Describe(OperationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.IsSuccess)
            return new(result.Kind == OperationOutcomeKind.Applied ? "Изменение подтверждено проверкой." : "Требуемая настройка уже выполнена.", OperationUserAction.None);
        if (result.Kind == OperationOutcomeKind.Unknown)
            return result.Code == OperationErrorCode.JournalUnavailable
                ? new("Результат изменения не подтверждён. Восстановите доступ к журналу перед проверкой состояния.", OperationUserAction.CheckJournal)
                : new("Результат изменения неизвестен. Сначала проверьте состояние сервиса; не отправляйте изменение повторно.", OperationUserAction.Observe);
        var message = result.Code switch
        {
            OperationErrorCode.InvalidRequest => "Проверьте параметры операции.",
            OperationErrorCode.IdentityMismatch => "Идентичность ресурса или профиля изменилась. Пересоздайте план после проверки.",
            OperationErrorCode.OwnershipNotProven => "Не удалось подтвердить принадлежность ресурса этой установке.",
            OperationErrorCode.ResourceConflict => "Обнаружена несовместимая настройка. Требуется проверка плана.",
            OperationErrorCode.ObservationChanged => "Состояние изменилось после наблюдения. Требуется новый план.",
            OperationErrorCode.VersionUnsupported => "Версия сервиса не соответствует проверенному профилю.",
            OperationErrorCode.AuthenticationRequired => "Требуется повторный вход в сервис.",
            OperationErrorCode.ResponseInvalid => "Сервис вернул ответ, который не соответствует проверенному контракту.",
            OperationErrorCode.ReadFailed => "Не удалось получить состояние сервиса. Проверьте соединение.",
            OperationErrorCode.DeadlineExceeded => "Истекло время ожидания состояния сервиса. Проверьте соединение.",
            OperationErrorCode.WriteRejected => "Сервис отклонил изменение до его применения. Проверьте параметры.",
            OperationErrorCode.WriteUncertain => "Результат изменения неизвестен. Сначала проверьте состояние сервиса.",
            OperationErrorCode.VerificationFailed => "Проверка не подтвердила требуемый результат.",
            OperationErrorCode.JournalUnavailable => "Журнал недоступен. Восстановите доступ к нему перед продолжением.",
            OperationErrorCode.OperationInProgress => "Другая операция ещё выполняется.",
            OperationErrorCode.Cancelled => "Операция остановлена до отправки изменения.",
            _ => "Требуется проверка состояния и плана.",
        };
        if (result.Code == OperationErrorCode.JournalUnavailable)
            return new(message, OperationUserAction.CheckJournal);
        var action = result.Code switch
        {
            OperationErrorCode.AuthenticationRequired => OperationUserAction.SignIn,
            OperationErrorCode.VersionUnsupported => OperationUserAction.CheckCompatibility,
            OperationErrorCode.InvalidRequest => OperationUserAction.CorrectInput,
            OperationErrorCode.ReadFailed or OperationErrorCode.DeadlineExceeded => OperationUserAction.CheckConnection,
            OperationErrorCode.OperationInProgress or OperationErrorCode.Cancelled => OperationUserAction.None,
            _ => OperationUserAction.ReviewPlan,
        };
        return new(message, action);
    }
}
