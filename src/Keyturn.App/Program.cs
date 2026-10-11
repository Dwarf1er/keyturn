using Avalonia;
using System;
using System.Linq;

namespace Keyturn.App;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--self-test")) return SelfTest.Run();

        // Scheduled launches must be invisible unless something needs attention, so decide
        // before Avalonia (and any window) exists. Store errors fall through to an error window.
        if (!args.Contains("--manage"))
        {
            try
            {
                var only = OnlyArg(args);
                var due = new SecretVault().Due().Where(s => only is null || s.Definition.Name.Equals(only, StringComparison.OrdinalIgnoreCase));
                if (!due.Any()) return 0;
            }
            catch { /* shown by App */ }
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>--only NAME limits the prompt to one secret (used by "keyturn get --refresh").</summary>
    public static string? OnlyArg(string[]? args)
    {
        var i = Array.IndexOf(args ?? [], "--only");
        return i >= 0 && i + 1 < args!.Length ? args[i + 1] : null;
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // The X11 window class is what a Linux desktop matches against a launcher's StartupWMClass to pick its icon.
            .With(new X11PlatformOptions { WmClass = "Keyturn.App" })
            .WithInterFont()
            .LogToTrace();
}
