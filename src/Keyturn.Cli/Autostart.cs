using System.Diagnostics;
using System.Security;
using System.Text;

namespace Keyturn.Cli;

/// <summary>Starts the background watcher at login and restarts it if it dies, using whatever the OS offers for that.</summary>
public interface IAutostart
{
    bool IsInstalled();
    int Install(string appPath, bool printOnly);
    int Uninstall();
    int Start();
}

/// <summary>
/// Linux: a systemd user service. macOS: a launch agent. Windows: a Task Scheduler task. The OS never polls: its only job
/// is to keep exactly one watcher alive (Restart=on-failure, KeepAlive on crash, restart-on-failure) from login on.
/// Also removes the hourly polling job the first versions installed.
/// </summary>
sealed class OsAutostart : IAutostart
{
    // launchd label and bundle id share the author's reverse-DNS domain (the bundle id is set in scripts/make-macos-app.sh).
    const string MacLabel = "com.antoinepoulin.keyturn.watch";
    const string LegacyMacLabel = "com.antoinepoulin.keyturn.prompt";
    const string BundleId = "com.antoinepoulin.keyturn";
    const string LinuxUnit = "keyturn-watch.service";
    const string WindowsTask = "KeyturnWatch";
    const string LegacyWindowsTask = "Keyturn";

    public bool IsInstalled()
    {
        if (OperatingSystem.IsLinux()) return File.Exists(Path.Combine(SystemdDir, LinuxUnit));
        if (OperatingSystem.IsMacOS()) return File.Exists(MacPlist(MacLabel));
        if (OperatingSystem.IsWindows()) return RunQuiet("schtasks", "/Query", "/TN", WindowsTask) == 0;
        return false;
    }

    public int Install(string appPath, bool printOnly)
    {
        appPath = Path.GetFullPath(appPath);
        if (!File.Exists(appPath)) throw new ArgumentException($"App not found: {appPath} (pass --app PATH)");

        if (printOnly)
        {
            if (OperatingSystem.IsLinux()) Console.WriteLine($"# {Path.Combine(SystemdDir, LinuxUnit)}\n{SystemdUnit(appPath)}");
            else if (OperatingSystem.IsMacOS()) Console.WriteLine($"# {MacPlist(MacLabel)}\n{MacLaunchAgent(appPath)}");
            else if (OperatingSystem.IsWindows()) Console.WriteLine(WindowsTaskXml(appPath, WindowsUser()));
            else throw new PlatformNotSupportedException();
            return 0;
        }

        RemoveLegacy();
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(SystemdDir);
            File.WriteAllText(Path.Combine(SystemdDir, LinuxUnit), SystemdUnit(appPath) + "\n");
            Run("systemctl", "--user", "daemon-reload");
            return Run("systemctl", "--user", "enable", "--now", LinuxUnit);
        }
        if (OperatingSystem.IsMacOS())
        {
            var plist = MacPlist(MacLabel);
            Directory.CreateDirectory(Path.GetDirectoryName(plist)!);
            File.WriteAllText(plist, MacLaunchAgent(appPath) + "\n");
            RunQuiet("launchctl", "unload", plist);
            return Run("launchctl", "load", "-w", plist);
        }
        if (OperatingSystem.IsWindows())
        {
            // From XML, because `schtasks /Create` flags can't express what a long-running watcher needs: no time limit
            // (the default kills a task after 72 hours), run on battery, catch up after the PC was off, restart on failure.
            var xml = Path.Combine(Path.GetTempPath(), $"keyturn-task-{Guid.NewGuid():N}.xml");
            try
            {
                File.WriteAllText(xml, WindowsTaskXml(appPath, WindowsUser()), Encoding.Unicode); // Task Scheduler wants UTF-16
                return Run("schtasks", "/Create", "/TN", WindowsTask, "/XML", xml, "/F");
            }
            finally { File.Delete(xml); }
        }
        throw new PlatformNotSupportedException();
    }

    public int Uninstall()
    {
        if (OperatingSystem.IsLinux())
        {
            RunQuiet("systemctl", "--user", "disable", "--now", LinuxUnit);
            Delete(Path.Combine(SystemdDir, LinuxUnit));
            RunQuiet("systemctl", "--user", "daemon-reload");
        }
        else if (OperatingSystem.IsMacOS())
        {
            RunQuiet("launchctl", "unload", MacPlist(MacLabel));
            Delete(MacPlist(MacLabel));
        }
        else if (OperatingSystem.IsWindows())
        {
            RunQuiet("schtasks", "/Delete", "/TN", WindowsTask, "/F");
        }
        RemoveLegacy();
        return 0;
    }

    public int Start()
    {
        if (OperatingSystem.IsLinux()) return Run("systemctl", "--user", "start", LinuxUnit);
        if (OperatingSystem.IsMacOS()) return Run("launchctl", "start", MacLabel);
        if (OperatingSystem.IsWindows()) return Run("schtasks", "/Run", "/TN", WindowsTask);
        throw new PlatformNotSupportedException();
    }

    // ---- what gets written (internal so the tests can check it on any OS) -------------------------------------------

    internal static string SystemdUnit(string app) => $"""
        [Unit]
        Description=Keyturn: ask for a new value when a secret expires
        After=graphical-session.target

        [Service]
        ExecStart={SystemdQuote(app)} --watch
        Restart=on-failure
        RestartSec=10

        [Install]
        WantedBy=default.target
        """;

    /// <summary>systemd treats % and $ specially even inside quotes.</summary>
    internal static string SystemdQuote(string path) =>
        "\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("%", "%%").Replace("$", "$$") + "\"";

    internal static string MacLaunchAgent(string app) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>Label</key><string>{MacLabel}</string>
          <key>ProgramArguments</key><array><string>{SecurityElement.Escape(app)}</string><string>--watch</string></array>
          <key>RunAtLoad</key><true/>
          <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
          <key>ProcessType</key><string>Interactive</string>
          <key>LimitLoadToSessionType</key><string>Aqua</string>
          <key>AssociatedBundleIdentifiers</key><array><string>{BundleId}</string></array>
        </dict></plist>
        """;

    internal static string WindowsTaskXml(string app, string user) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Keyturn: ask for a new value when a secret expires</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{SecurityElement.Escape(user)}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{SecurityElement.Escape(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <WakeToRun>false</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
            <RestartOnFailure>
              <Interval>PT1M</Interval>
              <Count>999</Count>
            </RestartOnFailure>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{SecurityElement.Escape(app)}</Command>
              <Arguments>--watch</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    // ---- plumbing -----------------------------------------------------------------------------------------------------

    static string SystemdDir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } x ? x
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "systemd", "user");

    static string MacPlist(string label) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", $"{label}.plist");

    static string WindowsUser() => $"{Environment.UserDomainName}\\{Environment.UserName}";

    /// <summary>The first versions polled hourly with a timer/launch agent/task; leaving it would prompt twice.</summary>
    static void RemoveLegacy()
    {
        if (OperatingSystem.IsLinux())
        {
            var timer = Path.Combine(SystemdDir, "keyturn.timer");
            var service = Path.Combine(SystemdDir, "keyturn.service");
            if (!File.Exists(timer) && !File.Exists(service)) return;
            RunQuiet("systemctl", "--user", "disable", "--now", "keyturn.timer");
            Delete(timer, service);
            RunQuiet("systemctl", "--user", "daemon-reload");
        }
        else if (OperatingSystem.IsMacOS())
        {
            var legacy = MacPlist(LegacyMacLabel);
            if (!File.Exists(legacy)) return;
            RunQuiet("launchctl", "unload", legacy);
            Delete(legacy);
        }
        else if (OperatingSystem.IsWindows())
        {
            RunQuiet("schtasks", "/Delete", "/TN", LegacyWindowsTask, "/F");
        }
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

    /// <summary>Same, without letting the tool's own output or errors reach the user (probes and best-effort cleanup).</summary>
    static int RunQuiet(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return 1;
        }
    }

    static void Delete(params string[] paths)
    {
        foreach (var p in paths) if (File.Exists(p)) File.Delete(p);
    }
}
