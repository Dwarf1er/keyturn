using System.Xml.Linq;
using Keyturn.Cli;

namespace Keyturn.Tests;

/// <summary>What gets written to the OS to keep the watcher alive, checked as text/XML on any OS.</summary>
public class AutostartFileTests
{
    static XElement Dict(string plist) =>
        XDocument.Parse(plist.Replace("<!DOCTYPE", "<!--").Replace("dtd\">", "dtd\"-->")).Root!.Element("dict")!; // the DTD is Apple's; no network in tests

    static XElement ValueOf(XElement dict, string key) => (XElement)dict.Elements("key").First(k => k.Value == key).NextNode!;

    [Fact]
    public void LaunchAgentRunsTheBundleExecutableInWatchModeAndRestartsOnlyOnFailure()
    {
        var app = "/Users/me/Applications/Keyturn.app/Contents/MacOS/Keyturn.App";
        var dict = Dict(OsAutostart.MacLaunchAgent(app));

        Assert.Equal("com.antoinepoulin.keyturn.watch", ValueOf(dict, "Label").Value);
        Assert.Equal([app, "--watch"], ValueOf(dict, "ProgramArguments").Elements("string").Select(e => e.Value));
        Assert.Equal("true", ValueOf(dict, "RunAtLoad").Name.LocalName);
        // Restart if it crashes, but a deliberate `keyturn watch stop` (exit 0) must stay stopped.
        Assert.Equal("false", ValueOf(ValueOf(dict, "KeepAlive"), "SuccessfulExit").Name.LocalName);
        Assert.Equal("Aqua", ValueOf(dict, "LimitLoadToSessionType").Value);
        Assert.Equal("com.antoinepoulin.keyturn", ValueOf(dict, "AssociatedBundleIdentifiers").Elements("string").Single().Value);
        Assert.DoesNotContain("StartInterval", OsAutostart.MacLaunchAgent(app)); // no polling any more
    }

    [Fact]
    public void LaunchAgentEscapesXmlSpecialCharactersInThePath()
    {
        var plist = OsAutostart.MacLaunchAgent("/Users/a&b/<x>/Keyturn.App");
        Assert.Contains("a&amp;b", plist);
        Assert.Contains("&lt;x&gt;", plist);
        Dict(plist); // still well-formed
    }

    [Fact]
    public void SystemdUnitIsALongRunningServiceThatRestartsOnFailure()
    {
        var unit = OsAutostart.SystemdUnit("/home/me/.local/bin/Keyturn.App");
        Assert.Contains("ExecStart=\"/home/me/.local/bin/Keyturn.App\" --watch", unit);
        Assert.Contains("Restart=on-failure", unit);
        Assert.Contains("WantedBy=default.target", unit);
        Assert.DoesNotContain("Type=oneshot", unit);
        Assert.DoesNotContain("OnUnitActiveSec", unit); // no polling timer
    }

    [Theory]
    [InlineData("/plain/path", "\"/plain/path\"")]
    [InlineData("/with space/Keyturn.App", "\"/with space/Keyturn.App\"")]
    [InlineData("/100%/Keyturn.App", "\"/100%%/Keyturn.App\"")]      // systemd expands % even inside quotes
    [InlineData("/$HOME/Keyturn.App", "\"/$$HOME/Keyturn.App\"")]    // ... and $
    [InlineData("/a\"b/Keyturn.App", "\"/a\\\"b/Keyturn.App\"")]
    public void SystemdQuotingEscapesWhatSystemdInterprets(string path, string expected) =>
        Assert.Equal(expected, OsAutostart.SystemdQuote(path));

    static readonly XNamespace Task = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void WindowsTaskRunsAtLogonForeverOnBatteryAndCatchesUpAfterTheMachineWasOff()
    {
        var xml = XDocument.Parse(OsAutostart.WindowsTaskXml(@"C:\Users\me\AppData\Local\Programs\Keyturn\Keyturn.App.exe", @"PC\me"));
        var root = xml.Root!;
        var settings = root.Element(Task + "Settings")!;
        string S(string name) => settings.Element(Task + name)!.Value;

        Assert.Equal(@"PC\me", root.Element(Task + "Triggers")!.Element(Task + "LogonTrigger")!.Element(Task + "UserId")!.Value);
        Assert.Equal("PT0S", S("ExecutionTimeLimit"));            // the default would kill the watcher after 72 hours
        Assert.Equal("false", S("DisallowStartIfOnBatteries"));   // schtasks's defaults skip laptops on battery
        Assert.Equal("false", S("StopIfGoingOnBatteries"));
        Assert.Equal("true", S("StartWhenAvailable"));
        Assert.Equal("IgnoreNew", S("MultipleInstancesPolicy"));
        Assert.Equal("999", settings.Element(Task + "RestartOnFailure")!.Element(Task + "Count")!.Value);

        var exec = root.Element(Task + "Actions")!.Element(Task + "Exec")!;
        Assert.Equal(@"C:\Users\me\AppData\Local\Programs\Keyturn\Keyturn.App.exe", exec.Element(Task + "Command")!.Value);
        Assert.Equal("--watch", exec.Element(Task + "Arguments")!.Value);
    }

    [Fact]
    public void WindowsTaskEscapesXmlSpecialCharacters()
    {
        var xml = OsAutostart.WindowsTaskXml(@"C:\R&D\Keyturn.App.exe", @"DOMAIN\o'brien & co");
        var root = XDocument.Parse(xml).Root!; // would throw if not well-formed
        Assert.Equal(@"C:\R&D\Keyturn.App.exe", root.Element(Task + "Actions")!.Element(Task + "Exec")!.Element(Task + "Command")!.Value);
    }
}

/// <summary>The `keyturn watch` commands, with a fake OS service manager so no real service is ever touched.</summary>
public sealed class WatchCliTests : IDisposable
{
    sealed class FakeAutostart : IAutostart
    {
        public bool Installed;
        public int InstallResult;
        public List<string> Calls = new();
        public Action? OnStart;
        public bool IsInstalled() => Installed;
        public int Install(string appPath, bool printOnly) { Calls.Add(printOnly ? "install --print" : "install"); if (!printOnly && InstallResult == 0) Installed = true; return InstallResult; }
        public int Uninstall() { Calls.Add("uninstall"); Installed = false; return 0; }
        public int Start() { Calls.Add("start"); OnStart?.Invoke(); return 0; }
    }

    sealed class NullBackend : ISecretBackend
    {
        public string? Get(string name) => "x";
        public void Set(string name, string value) { }
        public void Remove(string name) { }
    }

    sealed class NoPrompt : IPromptLauncher
    {
        public PromptOutcome Launch(string secretName, TimeSpan timeout, out string? detail) { detail = null; return PromptOutcome.Unavailable; }
    }

    static readonly CliTimeouts Fast = new(TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(600));
    // For tests where a (pretend) watcher is expected to honour the stop request: generous, so a starved CI runner can't make the
    // CLI give up (and delete the request) before the pretend watcher's thread has even been scheduled.
    static readonly CliTimeouts Patient = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T10:00:00+00:00");

    readonly string _dir = Directory.CreateTempSubdirectory("keyturn-watchcli").FullName;
    readonly FakeAutostart _autostart = new();
    WatchLock? _watcherInTheBackground;
    readonly string _dummyApp;

    public WatchCliTests()
    {
        _dummyApp = Path.Combine(_dir, "Keyturn.App");
        File.WriteAllText(_dummyApp, "stub");
    }

    public void Dispose()
    {
        _watcherInTheBackground?.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    SecretVault Vault() => new(new NullBackend(), Path.Combine(_dir, "secrets.json"), () => T0);

    record Result(int Code, string Out, string Err);

    Result Run(params string[] args) => RunWith(Fast, args);

    Result RunWith(CliTimeouts timeouts, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var io = new CliIo(o, e, TextReader.Null, true, true, () => "");
        var code = new KeyturnCli(Vault(), io, new NoPrompt(), () => T0, _autostart, timeouts).Run(args);
        return new Result(code, o.ToString(), e.ToString());
    }

    void WatcherIsRunning() => _watcherInTheBackground = WatchLock.TryAcquire(_dir);

    /// <summary>Plays the watcher: holds the lock, and lets go once a stop is requested (as the real one does).</summary>
    Task PretendWatcherThatHonoursStop()
    {
        WatcherIsRunning();
        return Task.Run(() =>
        {
            // Bounded: if the request never comes, fail the test instead of hanging the whole run.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (!WatchControl.StopRequested(_dir))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("the stop request never arrived");
                Thread.Sleep(5);
            }
            _watcherInTheBackground!.Dispose();
            _watcherInTheBackground = null;
        });
    }

    // ---- status ----------------------------------------------------------------------------------------------------

    [Fact]
    public void StatusWhenNotRunningExits2AndSaysHowToFixIt()
    {
        var r = Run("watch", "status");
        Assert.Equal(2, r.Code);
        Assert.Contains("not running", r.Out);
        Assert.Contains("keyturn watch install", r.Out);
    }

    [Fact]
    public void StatusWhenInstalledButStoppedPointsAtStart()
    {
        _autostart.Installed = true;
        Assert.Contains("keyturn watch start", Run("watch", "status").Out);
    }

    [Fact]
    public void StatusWhenRunningExits0WithThePid()
    {
        WatcherIsRunning();
        var r = Run("watch", "status");
        Assert.Equal(0, r.Code);
        Assert.Contains($"pid {Environment.ProcessId}", r.Out);
    }

    [Fact]
    public void StatusShowsTheNextExpiryAndWhatIsSnoozed()
    {
        var v = Vault();
        v.Define("later", "5h"); v.Set("later", "x");
        v.Define("soon", "2h"); v.Set("soon", "x");
        v.Define("dismissed", "1d");
        new SnoozeStore(_dir, () => T0).Snooze(v.Find("dismissed")!, TimeSpan.FromMinutes(10));

        var text = Run("watch", "status").Out;
        Assert.Contains("soon expires", text);
        Assert.Contains("snoozed:   dismissed", text);
    }

    [Fact]
    public void StatusListsANewSecretStillInItsGracePeriodAsSnoozed()
    {
        Vault().Define("fresh-and-empty", "1d"); // created "now" in the fake clock, no value
        var text = Run("watch", "status").Out;
        Assert.Contains("snoozed:   fresh-and-empty until", text);
    }

    [Fact]
    public void StatusJsonHasAStableShape()
    {
        var v = Vault();
        v.Define("vpn", "2h"); v.Set("vpn", "x");
        WatcherIsRunning();
        using var doc = System.Text.Json.JsonDocument.Parse(Run("watch", "status", "--json").Out);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("running").GetBoolean());
        Assert.False(root.GetProperty("installed").GetBoolean());
        Assert.Equal(Environment.ProcessId, root.GetProperty("pid").GetInt32());
        Assert.Equal("vpn", root.GetProperty("next").GetProperty("name").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Array, root.GetProperty("snoozed").ValueKind);
        Assert.EndsWith("watch.log", root.GetProperty("log").GetString());
    }

    // ---- install / start -------------------------------------------------------------------------------------------

    [Fact]
    public void InstallPrintOnlyWritesNothingAndStartsNothing()
    {
        var r = Run("watch", "install", "--app", _dummyApp, "--print");
        Assert.Equal(0, r.Code);
        Assert.Equal(["install --print"], _autostart.Calls);
    }

    [Fact]
    public void InstallWithoutFindingTheAppIsAUsageError()
    {
        var r = Run("watch", "install", "--app", Path.Combine(_dir, "does-not-exist"));
        Assert.Equal(1, r.Code);
        Assert.Contains("App not found", r.Err);
        Assert.Empty(_autostart.Calls);
    }

    [Fact]
    public void InstallStartsTheWatcherAndSaysSo()
    {
        _autostart.OnStart = WatcherIsRunning; // like Task Scheduler on Windows, which has to be told to run it now
        var r = Run("watch", "install", "--app", _dummyApp);
        Assert.Equal(0, r.Code);
        Assert.Contains("install", _autostart.Calls);
        Assert.Contains("start", _autostart.Calls);
        Assert.Contains("the watcher is running", r.Err);
        Assert.Equal("", r.Out); // diagnostics only on stderr
    }

    [Fact]
    public void InstallDoesNotNudgeAWatcherThatAlreadyStartedItself()
    {
        _autostart.OnStart = () => throw new InvalidOperationException("must not be called");
        // systemd/launchd start it as part of installing: simulate by it being up before the CLI looks.
        WatcherIsRunning();
        var r = Run("watch", "install", "--app", _dummyApp);
        Assert.Equal(0, r.Code);
        Assert.DoesNotContain("start", _autostart.Calls);
    }

    [Fact]
    public void InstallThatCannotStartItYetSaysHowToStartItLater()
    {
        var r = Run("watch", "install", "--app", _dummyApp); // nothing ever takes the lock
        Assert.Equal(0, r.Code);
        Assert.Contains("keyturn watch start", r.Err);
    }

    [Fact]
    public void InstallFailureIsReportedAndNothingIsClaimed()
    {
        _autostart.InstallResult = 1;
        var r = Run("watch", "install", "--app", _dummyApp);
        Assert.Equal(1, r.Code);
        Assert.Contains("couldn't set the watcher up", r.Err);
        Assert.DoesNotContain("is running", r.Err);
    }

    [Fact]
    public void StartWhenAlreadyRunningDoesNothing()
    {
        WatcherIsRunning();
        _autostart.Installed = true;
        var r = Run("watch", "start");
        Assert.Equal(0, r.Code);
        Assert.Contains("already running", r.Err);
        Assert.DoesNotContain("start", _autostart.Calls);
    }

    [Fact]
    public void StartWhenNotSetUpPointsAtInstall()
    {
        var r = Run("watch", "start");
        Assert.Equal(1, r.Code);
        Assert.Contains("keyturn watch install", r.Err);
    }

    [Fact]
    public void StartAsksTheOsAndWaitsForTheWatcher()
    {
        _autostart.Installed = true;
        _autostart.OnStart = WatcherIsRunning;
        var r = Run("watch", "start");
        Assert.Equal(0, r.Code);
        Assert.Equal(["start"], _autostart.Calls);
    }

    // ---- stop / uninstall ------------------------------------------------------------------------------------------

    [Fact]
    public void StopWhenNotRunningIsFine()
    {
        var r = Run("watch", "stop");
        Assert.Equal(0, r.Code);
        Assert.Contains("isn't running", r.Err);
        Assert.False(WatchControl.StopRequested(_dir));
    }

    [Fact]
    public async Task StopAsksNicelyThroughTheStopFileAndCleansUpAfterwards()
    {
        var pretend = PretendWatcherThatHonoursStop();
        var r = RunWith(Patient, "watch", "stop");
        await pretend;
        Assert.Equal(0, r.Code);
        Assert.Contains("stopped the watcher", r.Err);
        Assert.False(WatchControl.IsRunning(_dir));
        Assert.False(WatchControl.StopRequested(_dir), "a leftover stop request would stop the next watcher at once");
    }

    [Fact]
    public void StopThatIsIgnoredFailsInsteadOfPretendingAndNeverKillsAnUnrelatedProcess()
    {
        WatcherIsRunning(); // holds the lock and never lets go; its recorded pid is this test host, which is not Keyturn
        var r = Run("watch", "stop");
        Assert.Equal(1, r.Code);
        Assert.Contains("couldn't stop", r.Err);
        Assert.False(WatchControl.StopRequested(_dir));
    }

    [Fact]
    public async Task UninstallStopsTheWatcherThenRemovesTheLoginEntry()
    {
        _autostart.Installed = true;
        var pretend = PretendWatcherThatHonoursStop();
        var r = RunWith(Patient, "watch", "uninstall");
        await pretend;
        Assert.Equal(0, r.Code);
        Assert.False(WatchControl.IsRunning(_dir));
        Assert.Equal(["uninstall"], _autostart.Calls);
        Assert.False(_autostart.Installed);
    }

    [Fact]
    public void UninstallWorksWhenNothingIsRunning()
    {
        _autostart.Installed = true;
        Assert.Equal(0, Run("watch", "uninstall").Code);
        Assert.Equal(["uninstall"], _autostart.Calls);
    }

    [Fact]
    public void UnknownWatchSubcommandIsAUsageError() => Assert.Equal(1, Run("watch", "dance").Code);

    // ---- the warning --------------------------------------------------------------------------------------------------

    [Fact]
    public void AddingASecretWarnsWhenNothingIsWatching()
    {
        var r = Run("add", "vpn", "1h");
        Assert.Equal(0, r.Code);
        Assert.Equal("", r.Out);
        Assert.Contains("the watcher isn't running", r.Err);
    }

    [Fact]
    public void AddingASecretIsQuietWhenTheWatcherIsRunning()
    {
        WatcherIsRunning();
        Assert.DoesNotContain("watcher", Run("add", "vpn", "1h").Err.Replace("Defined", ""));
    }

    [Fact]
    public void ListWarnsOnlyWhenSomethingNeedsAttentionAndNothingIsWatching()
    {
        Assert.DoesNotContain("watcher", Run("list").Err);              // nothing defined, nothing due
        var v = Vault(); v.Define("vpn", "1h");                          // never set: due
        Assert.Contains("the watcher isn't running", Run("list").Err);
        Assert.DoesNotContain("watcher", Run("list", "--json").Err);     // scripts get clean stderr
        WatcherIsRunning();
        Assert.DoesNotContain("watcher", Run("list").Err);
    }
}
