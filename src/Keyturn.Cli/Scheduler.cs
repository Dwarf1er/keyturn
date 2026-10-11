using System.Diagnostics;
using System.Security;

namespace Keyturn;

/// <summary>
/// Installs an OS-level job that launches the Keyturn app at login and hourly. The app exits
/// immediately (no window) unless a secret is due, so the cadence only bounds how late a prompt can be.
/// </summary>
static class Scheduler
{
    // launchd label and bundle id share the author's reverse-DNS domain (the bundle id is set in scripts/make-macos-app.sh).
    const string Id = "com.antoinepoulin.keyturn.prompt";
    const string BundleId = "com.antoinepoulin.keyturn";

    public static int Install(string appPath, bool printOnly)
    {
        appPath = Path.GetFullPath(appPath);
        if (!File.Exists(appPath)) throw new ArgumentException($"App not found: {appPath} (pass --app PATH)");

        if (OperatingSystem.IsLinux()) return Linux(appPath, printOnly);
        if (OperatingSystem.IsMacOS()) return Mac(appPath, printOnly);
        if (OperatingSystem.IsWindows()) return Windows(appPath, printOnly);
        throw new PlatformNotSupportedException();
    }

    public static int Uninstall()
    {
        if (OperatingSystem.IsLinux())
        {
            Run("systemctl", "--user", "disable", "--now", "keyturn.timer");
            Delete(Path.Combine(SystemdDir, "keyturn.timer"), Path.Combine(SystemdDir, "keyturn.service"));
            Run("systemctl", "--user", "daemon-reload");
        }
        else if (OperatingSystem.IsMacOS())
        {
            Run("launchctl", "unload", MacPlist);
            Delete(MacPlist);
        }
        else if (OperatingSystem.IsWindows())
        {
            Run("schtasks", "/Delete", "/TN", "Keyturn", "/F");
        }
        Console.WriteLine("Removed the scheduled prompt.");
        return 0;
    }

    static string SystemdDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "systemd", "user");

    static string MacPlist => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", $"{Id}.plist");

    static int Linux(string app, bool printOnly)
    {
        var service = $"""
            [Unit]
            Description=Keyturn: prompt for rotating secrets that are due

            [Service]
            Type=oneshot
            ExecStart="{app}"
            """;
        var timer = """
            [Unit]
            Description=Check hourly whether any Keyturn secret is due

            [Timer]
            OnStartupSec=1min
            OnUnitActiveSec=1h
            Persistent=true

            [Install]
            WantedBy=timers.target
            """;

        if (printOnly)
        {
            Console.WriteLine($"# {SystemdDir}/keyturn.service\n{service}\n\n# {SystemdDir}/keyturn.timer\n{timer}");
            return 0;
        }

        Directory.CreateDirectory(SystemdDir);
        File.WriteAllText(Path.Combine(SystemdDir, "keyturn.service"), service + "\n");
        File.WriteAllText(Path.Combine(SystemdDir, "keyturn.timer"), timer + "\n");
        Run("systemctl", "--user", "daemon-reload");
        var rc = Run("systemctl", "--user", "enable", "--now", "keyturn.timer");
        Console.WriteLine("Installed systemd user timer 'keyturn.timer'. The service needs your session's DISPLAY/WAYLAND_DISPLAY;\n" +
                          "if prompts never appear, run: systemctl --user import-environment DISPLAY WAYLAND_DISPLAY");
        return rc;
    }

    internal static string MacLaunchAgent(string app) =>
        $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>Label</key><string>{Id}</string>
              <key>ProgramArguments</key><array><string>{SecurityElement.Escape(app)}</string></array>
              <key>RunAtLoad</key><true/>
              <key>StartInterval</key><integer>3600</integer>
              <key>ProcessType</key><string>Interactive</string>
              <key>LimitLoadToSessionType</key><string>Aqua</string>
              <key>AssociatedBundleIdentifiers</key><array><string>{BundleId}</string></array>
            </dict></plist>
            """;

    static int Mac(string app, bool printOnly)
    {
        var plist = MacLaunchAgent(app);
        if (printOnly) { Console.WriteLine($"# {MacPlist}\n{plist}"); return 0; }

        Directory.CreateDirectory(Path.GetDirectoryName(MacPlist)!);
        File.WriteAllText(MacPlist, plist + "\n");
        Run("launchctl", "unload", MacPlist);
        var rc = Run("launchctl", "load", MacPlist);
        Console.WriteLine($"Installed launch agent {Id}.");
        return rc;
    }

    static int Windows(string app, bool printOnly)
    {
        // Hourly, repeating from creation; a missed run is caught up as soon as the PC is available.
        string[] args = ["/Create", "/F", "/TN", "Keyturn", "/TR", $"\"{app}\"", "/SC", "HOURLY", "/RL", "LIMITED"];
        if (printOnly) { Console.WriteLine("schtasks " + string.Join(' ', args)); return 0; }

        var rc = Run("schtasks", args);
        if (rc == 0) Console.WriteLine("Installed scheduled task 'Keyturn' (hourly).");
        return rc;
    }

    static int Run(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"keyturn: '{file}' not found.");
            return 1;
        }
    }

    static void Delete(params string[] paths)
    {
        foreach (var p in paths) if (File.Exists(p)) File.Delete(p);
    }
}
