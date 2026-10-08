using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ConfidentialDataMasker.Services;
using ConfidentialDataMasker.ViewModels;

namespace ConfidentialDataMasker;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {

            var mainWindow = new MainWindow();
            var filePicker = new FilePickerService(mainWindow);
            var mainWindowViewModel = new MainWindowViewModel(filePicker);
            mainWindow.DataContext = mainWindowViewModel;
            desktop.MainWindow = mainWindow;

            desktop.ShutdownRequested += (_, _) => mainWindowViewModel.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
