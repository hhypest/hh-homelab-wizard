using Avalonia.Threading;

namespace HhHomelab.Wizard.Desktop.Services;

public interface IUiDispatcher { Task InvokeAsync(Action action); }
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public async Task InvokeAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else await Dispatcher.UIThread.InvokeAsync(action);
    }
}
