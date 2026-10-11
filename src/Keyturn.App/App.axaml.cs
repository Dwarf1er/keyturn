using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Keyturn.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var manage = desktop.Args?.Contains("--manage") == true;
            try
            {
                desktop.MainWindow = new MainWindow(new SecretVault(), manage, Program.OnlyArg(desktop.Args?.ToArray()));
            }
            catch (Exception ex)
            {
                desktop.MainWindow = new ErrorWindow(ex.Message);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
