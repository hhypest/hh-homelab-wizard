using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HhHomelab.Wizard.Application;
using HhHomelab.Wizard.Desktop.Services;
using HhHomelab.Wizard.Domain;

namespace HhHomelab.Wizard.Desktop.ViewModels;

public enum WizardPage { Connection, Plan, Progress, Result }
public sealed record DemoChoice(string Label, DemoBehavior Behavior);

public sealed class MainWindowViewModel : ObservableObject, IWizardObserver, IUserConfirmation
{
    private readonly IDemoScenario scenario;
    private readonly IUiDispatcher dispatcher;
    private TaskCompletionSource? confirmation;
    private CancellationTokenSource? runCancellation;
    private Task? activeRun;
    private WizardPage page;
    private string host = "192.0.2.10", user = "homelab", validation = "";
    private WizardProgress? progress;
    private bool busy, closing;
    private DemoChoice selectedDemo;
    public MainWindowViewModel(IDemoScenario scenario, IUiDispatcher dispatcher)
    {
        this.scenario = scenario; this.dispatcher = dispatcher; selectedDemo = DemoChoices[0];
        ContinueCommand = new RelayCommand(Continue, () => !IsBusy && !closing);
        BackCommand = new RelayCommand(() => { if (!IsBusy && !closing) Page = WizardPage.Connection; }, () => !IsBusy && !closing);
        StartCommand = new AsyncRelayCommand(StartAsync, () => Page == WizardPage.Plan && !IsBusy && !closing);
        StopCommand = new RelayCommand(() => { runCancellation?.Cancel(); NotifyState(); }, () => IsBusy && runCancellation is { IsCancellationRequested: false });
        ConfirmCommand = new RelayCommand(() => { if (IsWaiting && !closing) confirmation?.TrySetResult(); NotifyState(); }, () => IsWaiting && confirmation is { Task.IsCompleted: false } && !closing);
        NewDemoCommand = new RelayCommand(() => { if (IsBusy || closing) return; progress = null; Page = WizardPage.Connection; NotifyState(); }, () => !IsBusy && !closing);
    }
    public IReadOnlyList<DemoChoice> DemoChoices { get; } = [new("Успешная настройка", DemoBehavior.Success), new("Ошибка настройки Media", DemoBehavior.Failure), new("Потеря ответа при сопряжении", DemoBehavior.LostResponse)];
    public DemoChoice SelectedDemo { get => selectedDemo; set => SetProperty(ref selectedDemo, value); }
    public string Host { get => host; set { if (SetProperty(ref host, value)) Validation = ""; } }
    public string User { get => user; set { if (SetProperty(ref user, value)) Validation = ""; } }
    public string Validation { get => validation; private set { SetProperty(ref validation, value); OnPropertyChanged(nameof(HasValidation)); } }
    public bool HasValidation => Validation.Length != 0;
    public WizardPage Page { get => page; private set { SetProperty(ref page, value); NotifyState(); } }
    public bool IsBusy { get => busy; private set { SetProperty(ref busy, value); NotifyState(); } }
    public WizardProgress? Progress => progress;
    public bool IsConnection => Page == WizardPage.Connection;
    public bool IsPlan => Page == WizardPage.Plan;
    public bool IsProgress => Page == WizardPage.Progress;
    public bool IsResult => Page == WizardPage.Result;
    public bool IsWaiting => IsBusy && progress?.State == WizardState.WaitingForUser;
    public string StepLabel => Page switch { WizardPage.Connection => "Шаг 1 из 4 · Подключение", WizardPage.Plan => "Шаг 2 из 4 · План", WizardPage.Progress => "Шаг 3 из 4 · Выполнение", _ => "Шаг 4 из 4 · Результат" };
    public string Title => Page switch { WizardPage.Connection => "Познакомьтесь с мастером", WizardPage.Plan => "Ваш домашний сервер", WizardPage.Progress when IsWaiting => "Нужно ваше подтверждение", WizardPage.Progress => "Настраиваем ваш сервер", _ => progress?.State switch { WizardState.Succeeded => "Демонстрация завершена", WizardState.Cancelled => "Демонстрация остановлена", WizardState.OutcomeUnknown => "Результат операции неизвестен", _ => "Демонстрация завершилась ошибкой" } };
    public string Subtitle => Page switch { WizardPage.Connection => "Введите пример адреса NAS и пользователя. Подключение не выполняется.", WizardPage.Plan => "Media + Home Assistant на Synology. Пока это демонстрационный план.", WizardPage.Progress => "Показываем работу мастера на искусственном сценарии.", _ => "Реальные сервисы не создавались, настройки NAS не менялись." };
    public string CurrentOperation => progress?.Operation switch { WizardOperation.PrepareFolders => "Папки и конфигурация", WizardOperation.ConfigureMedia => "Медиасервисы и связи", WizardOperation.ConfigureHomeAssistant => "Первичная настройка Home Assistant", WizardOperation.PairDevice => "Подключение устройств", WizardOperation.ConfigureDashboard => "Панель и автоматизации", WizardOperation.Verify => "Итоговая проверка", _ => "Ожидание запуска" };
    public string ProgressLabel => $"Завершено {progress?.Completed ?? 0} из 6 групп операций";
    public double ProgressPercent => (progress?.Completed ?? 0) * 100.0 / 6;
    public string ResultMessage => progress?.State switch
    {
        WizardState.Succeeded => "Все шесть групп демонстрационных операций завершены. Это не подтверждение готовности настоящего стека.",
        WizardState.Cancelled => "Дальнейшие действия остановлены. Остановка отличается от отката: выполненные шаги сохраняются, а отправленная операция могла завершиться.",
        WizardState.OutcomeUnknown => "Ответ потерян. Повторное сопряжение недоступно. В настоящем сценарии сначала потребуется наблюдение состояния; оно появится на следующих этапах.",
        _ => progress?.Problem?.Message ?? "Демонстрация не завершилась успешно."
    };
    public string Diagnostic => progress?.Problem is { } error ? $"{error.Code}\n{error.Diagnostic}" : "Режим: демонстрация M1\nАдаптер: искусственный\nSSH/API/файловые операции: не выполняются\nЖурнал и возобновление после перезапуска: ещё не реализованы";
    public IRelayCommand ContinueCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IAsyncRelayCommand StartCommand { get; }
    public IRelayCommand StopCommand { get; }
    public IRelayCommand ConfirmCommand { get; }
    public IRelayCommand NewDemoCommand { get; }
    private void Continue()
    {
        if (IsBusy || closing) return;
        if (!new DemoRequest(Host.Trim(), User.Trim(), SelectedDemo.Behavior).IsValid)
        { Validation = "Укажите корректный адрес NAS (IP или имя) и пользователя, до 64 символов."; return; }
        Host = Host.Trim(); User = User.Trim(); Validation = ""; Page = WizardPage.Plan;
    }
    private async Task StartAsync()
    {
        // ICommand CanExecute is a UI hint; the method also guards programmatic execution.
        if (IsBusy || closing || Page != WizardPage.Plan) return;
        var request = new DemoRequest(Host, User, SelectedDemo.Behavior);
        if (!request.IsValid) { Validation = "Параметры изменились. Проверьте адрес и пользователя."; Page = WizardPage.Connection; return; }
        using var cancellation = new CancellationTokenSource();
        runCancellation = cancellation;
        activeRun = RunAsync(request, cancellation.Token);
        try { await activeRun; }
        finally { runCancellation = null; activeRun = null; }
    }
    private async Task RunAsync(DemoRequest request, CancellationToken token)
    {
        confirmation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        progress = new(WizardState.Running, WizardOperation.PrepareFolders, 0, 6);
        IsBusy = true; Page = WizardPage.Progress;
        try { await scenario.RunAsync(request, this, this, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { await ReportAsync(new(WizardState.Cancelled, progress!.Operation, progress.Completed, 6)); }
        catch (Exception) { await ReportAsync(new(WizardState.Failed, progress!.Operation, progress.Completed, 6, new("demo.ui.unexpected", "Не удалось завершить демонстрацию.", "Подробности исключения скрыты."))); }
        finally { confirmation = null; IsBusy = false; Page = WizardPage.Result; }
    }
    public Task ReportAsync(WizardProgress update) => dispatcher.InvokeAsync(() => { progress = update; NotifyState(); });
    public Task WaitAsync(CancellationToken token) => (confirmation ?? throw new InvalidOperationException("demo.confirmation.missing")).Task.WaitAsync(token);
    public async Task CloseAsync()
    {
        closing = true; NotifyState(); runCancellation?.Cancel();
        if (activeRun is { } task) await task;
    }
    private void NotifyState()
    {
        foreach (var name in new[] { nameof(Progress), nameof(IsConnection), nameof(IsPlan), nameof(IsProgress), nameof(IsResult), nameof(IsWaiting), nameof(StepLabel), nameof(Title), nameof(Subtitle), nameof(CurrentOperation), nameof(ProgressLabel), nameof(ProgressPercent), nameof(ResultMessage), nameof(Diagnostic) }) OnPropertyChanged(name);
        ContinueCommand?.NotifyCanExecuteChanged(); BackCommand?.NotifyCanExecuteChanged(); StartCommand?.NotifyCanExecuteChanged(); StopCommand?.NotifyCanExecuteChanged(); ConfirmCommand?.NotifyCanExecuteChanged(); NewDemoCommand?.NotifyCanExecuteChanged();
    }
}
