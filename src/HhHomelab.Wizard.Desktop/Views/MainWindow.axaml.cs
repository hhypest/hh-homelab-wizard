using Avalonia.Controls;
using HhHomelab.Wizard.Desktop.ViewModels;

namespace HhHomelab.Wizard.Desktop.Views;

public partial class MainWindow : Window
{
    private bool closePending;
    private bool mayClose;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (mayClose || DataContext is not MainWindowViewModel model)
            return;
        args.Cancel = true;
        if (closePending)
            return;
        closePending = true;
        await model.CloseAsync();
        mayClose = true;
        Close();
    }
}
