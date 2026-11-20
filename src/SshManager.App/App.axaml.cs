using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using SshManager.App.Services;
using SshManager.App.ViewModels;
using SshManager.App.Views;
using SshManager.Core.Services;

namespace SshManager.App;

public partial class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var profiles = new ProfileStore();
            var transfers = new TransferService();
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(profiles, transfers),
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
