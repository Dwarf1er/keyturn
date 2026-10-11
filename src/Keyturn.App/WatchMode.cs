using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Keyturn.App;

/// <summary>
/// <c>Keyturn.App --watch</c>: the background watcher. No window and no UI framework is started here (so it idles at a
/// few tens of MB); each prompt is a separate <c>Keyturn.App</c> process that exists only while it is open.
/// </summary>
static class WatchMode
{
    public static int Run(string[] args)
    {
        var vault = new SecretVault();
        var directory = vault.ConfigDirectory;

        // One watcher per user: a second one (a double-click, a service restart racing a login item) just exits.
        using var gate = WatchLock.TryAcquire(directory);
        if (gate is null) return 0;

        var log = new WatchLog(directory);
        WatchControl.ClearStopRequest(directory); // a leftover request must not stop a freshly started watcher

        // --snooze SECONDS is a testing knob; the real default is ten minutes.
        var options = SecondsArg(args, "--snooze") is { } seconds ? new WatcherOptions { Snooze = TimeSpan.FromSeconds(seconds) } : new WatcherOptions();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try
        {
            // SIGTERM is how systemd and launchd ask a service to stop.
            using var _ = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cts.Cancel(); });
            Execute(vault, directory, options, log, cts.Token);
        }
        catch (PlatformNotSupportedException)
        {
            Execute(vault, directory, options, log, cts.Token);
        }
        return 0;
    }

    static void Execute(SecretVault vault, string directory, WatcherOptions options, WatchLog log, CancellationToken token)
    {
        var watcher = new Watcher(vault, new SnoozeStore(directory), new ProcessPromptRunner(), options,
            stopRequested: () => WatchControl.StopRequested(directory), log: log.Write);
        try { watcher.RunAsync(token).GetAwaiter().GetResult(); }
        finally { WatchControl.ClearStopRequest(directory); }
    }

    static double? SecondsArg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length && double.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0 ? s : null;
    }
}

/// <summary>Opens the prompt window as its own process and waits for it to close.</summary>
sealed class ProcessPromptRunner : IPromptRunner
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("can't tell which executable is running");
        var start = new ProcessStartInfo(path) { UseShellExecute = false };
        start.ArgumentList.Add("--respect-snooze"); // secrets that were dismissed stay out of the window until their snooze ends

        // A watcher started at login may predate the desktop; the window needs to know where the display is.
        DesktopEnvironment.FillMissingFromSystemd(start.Environment);

        using var process = Process.Start(start) ?? throw new InvalidOperationException("the prompt did not start");
        await process.WaitForExitAsync(cancellationToken); // if the watcher is stopped meanwhile, the window is left open for the user
        return process.ExitCode;
    }
}
