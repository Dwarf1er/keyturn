using System.Diagnostics;

namespace Keyturn;

/// <summary>
/// On Linux the watcher is a systemd user service, which can start before the desktop exists and never learns about
/// <c>DISPLAY</c>/<c>WAYLAND_DISPLAY</c> afterwards (variables imported later only reach services started after the import).
/// The user manager does know them, so the watcher asks it when it opens a prompt, filling in only what it is missing.
/// </summary>
public static class DesktopEnvironment
{
    static readonly string[] Wanted = ["DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP"];

    /// <summary>Parses `systemctl --user show-environment` output (KEY=value lines), keeping only the desktop variables.</summary>
    public static Dictionary<string, string> Parse(string showEnvironmentOutput)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in showEnvironmentOutput.Split('\n'))
        {
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].TrimEnd('\r');
            // systemd quotes values with special characters as $'...'. Display variables never need that, so skip such lines
            // instead of mis-parsing them.
            if (!Wanted.Contains(key) || value.Length == 0 || value.StartsWith("$'") || value.StartsWith('"')) continue;
            result[key] = value;
        }
        return result;
    }

    /// <summary>Adds the manager's desktop variables to <paramref name="environment"/> where it has none. Linux only; never throws.</summary>
    public static void FillMissingFromSystemd(IDictionary<string, string?> environment)
    {
        if (!OperatingSystem.IsLinux()) return;
        try
        {
            var start = new ProcessStartInfo("systemctl", "--user show-environment")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var process = Process.Start(start);
            if (process is null) return;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(2000)) { process.Kill(); return; }
            foreach (var (key, value) in Parse(output.GetAwaiter().GetResult()))
                if (string.IsNullOrEmpty(environment.TryGetValue(key, out var current) ? current : null)) environment[key] = value;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // No systemd (or no user manager): the watcher's own environment is all there is.
        }
    }
}
