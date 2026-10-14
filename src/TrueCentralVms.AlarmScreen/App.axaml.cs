using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TrueCentralVms.AlarmScreen.ViewModels;
using TrueCentralVms.AlarmScreen.Views;

namespace TrueCentralVms.AlarmScreen;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = new MainViewModel();
            desktop.MainWindow = new MainWindow { DataContext = vm };
            desktop.ShutdownRequested += async (_, _) => await vm.StopAsync();
            vm.Start();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
