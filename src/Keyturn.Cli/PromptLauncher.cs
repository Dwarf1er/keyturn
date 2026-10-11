using System.Diagnostics;

namespace Keyturn.Cli;

public enum PromptOutcome
{
    /// <summary>The window was shown and has been closed (saved or dismissed); re-check the secret.</summary>
    Completed,
    TimedOut,
    /// <summary>The prompt could not be shown at all (no app, no graphical session).</summary>
    Unavailable,
}

/// <summary>Shows the Keyturn prompt for one secret and waits for it to close.</summary>
public interface IPromptLauncher
{
    PromptOutcome Launch(string secretName, TimeSpan timeout, out string? detail);
}

public sealed class ProcessPromptLauncher : IPromptLauncher
{
    public PromptOutcome Launch(string secretName, TimeSpan timeout, out string? detail)
    {
        detail = null;
        var app = FindApp();
        if (app is null)
        {
            detail = "can't find the Keyturn app. Put it next to the keyturn CLI (on macOS: Keyturn.app beside it, or in ~/Applications) or set KEYTURN_APP to its path.";
            return PromptOutcome.Unavailable;
        }

        if (OperatingSystem.IsLinux() &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            detail = "no graphical session (DISPLAY/WAYLAND_DISPLAY unset), so the prompt can't be shown.";
            return PromptOutcome.Unavailable;
        }

        var psi = new ProcessStartInfo(app) { UseShellExecute = false };
        psi.ArgumentList.Add("--only");
        psi.ArgumentList.Add(secretName);

        Process process;
        try { process = Process.Start(psi) ?? throw new InvalidOperationException("process did not start"); }
        catch (Exception ex)
        {
            detail = $"could not start the Keyturn app ({ex.Message}).";
            return PromptOutcome.Unavailable;
        }

        using (process)
        {
            if (process.WaitForExit(timeout)) return PromptOutcome.Completed;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* exited meanwhile */ }
            return PromptOutcome.TimedOut;
        }
    }

    static string? FindApp() => AppLocator.Find();
}
