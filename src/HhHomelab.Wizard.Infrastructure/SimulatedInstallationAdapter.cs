using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Infrastructure;

/// <summary>Artificial M1 adapter: never reads a NAS, sends network requests or creates files.</summary>
public sealed class SimulatedInstallationAdapter(TimeProvider clock) : IDemoInstallationAdapter
{
    public async Task<DemoOperationResult> ExecuteAsync(
        WizardOperation operation,
        DemoBehavior behavior,
        CancellationToken token
    )
    {
        await Task.Delay(TimeSpan.FromMilliseconds(450), clock, token).ConfigureAwait(false);
        if (behavior == DemoBehavior.Failure && operation == WizardOperation.ConfigureMedia)
            return new(
                new(
                    "demo.media.failed",
                    "Не удалось настроить медиасервисы в демонстрации.",
                    "Искусственный отказ Media. Подключений к сервисам не выполнялось."
                )
            );
        if (behavior == DemoBehavior.LostResponse && operation == WizardOperation.PairDevice)
            return new(
                new(
                    "demo.pairing.unknown",
                    "Ответ Home Assistant не получен. Результат сопряжения неизвестен.",
                    "Искусственная потеря ответа. Повтор сопряжения запрещён; реальное наблюдение будет добавлено позже."
                ),
                true
            );
        return new();
    }
}
