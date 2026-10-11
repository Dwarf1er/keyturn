using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Keyturn.Cli;

/// <summary>Everything the CLI touches, injected so the whole contract can be tested in-process.</summary>
public sealed record CliIo(
    TextWriter Out,
    TextWriter Err,
    TextReader In,
    bool InputRedirected,
    bool OutputRedirected,
    Func<string> ReadHidden);

/// <summary>
/// The supported interface for other tools. Contract:
/// <list type="bullet">
/// <item>stdout carries only data (a secret value, or JSON with --json); diagnostics go to stderr.</item>
/// <item>Exit codes: 0 ok, 1 usage error or unknown secret, 2 no value, 3 stale, 4 storage error (credential store or config file).</item>
/// <item><c>get</c> never prompts on the terminal; only <c>--refresh</c> may show the GUI prompt.</item>
/// </list>
/// </summary>
/// <summary>How long the CLI waits for the watcher to start or stop. Injectable so tests don't wait for real.</summary>
public sealed record CliTimeouts(TimeSpan WatcherStart, TimeSpan WatcherStop)
{
    public static CliTimeouts Default { get; } = new(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15));
}

public sealed class KeyturnCli(
    SecretVault vault, CliIo io, IPromptLauncher prompt, Func<DateTimeOffset>? now = null,
    IAutostart? autostart = null, CliTimeouts? timeouts = null)
{
    public const int Ok = 0, Usage = 1, Missing = 2, Stale = 3, StoreError = 4;
    public const int NotRunning = 2; // `watch status` only
    public static readonly TimeSpan DefaultRefreshTimeout = TimeSpan.FromMinutes(5);

    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    readonly IAutostart _autostart = autostart ?? new OsAutostart();
    readonly CliTimeouts _timeouts = timeouts ?? CliTimeouts.Default;

    public int Run(string[] argv)
    {
        try
        {
            return Dispatch(argv);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException or FormatException)
        {
            return Fail(ex.Message, Usage);
        }
        catch (Exception ex)
        {
            // Credential store failures (locked keyring, no D-Bus session, ...) and unreadable config land here.
            var detail = ex.InnerException is { } inner ? $" ({inner.Message})" : "";
            return Fail($"storage error: {ex.Message.Split('\n')[0]}{detail}", StoreError);
        }
    }

    int Dispatch(string[] argv)
    {
        if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help") return PrintUsage(argv.Length == 0 ? Usage : Ok);
        if (argv[0] is "--version" or "-v") { io.Out.WriteLine(VersionText()); return Ok; }
        var command = argv[0];
        var args = new Args(argv.Skip(1));

        return command switch
        {
            "list" => List(args),
            "status" => StatusCommand(args),
            "due" => Due(args),
            "add" => Add(args),
            "set" => Set(args),
            "get" => Get(args),
            "remove" => Remove(args),
            "run" => RunWith(argv.Skip(1).ToArray()),
            "watch" => Watch(args),
            _ => Fail($"unknown command '{command}'", Usage, printUsage: true),
        };
    }

    // ---- reading state ---------------------------------------------------------------------

    int List(Args a)
    {
        var json = a.Flag("--json");
        var statuses = vault.Statuses();
        Print(statuses, json);
        if (!json && statuses.Any(s => s.NeedsAttention)) WarnIfNotWatching();
        return Ok;
    }

    int StatusCommand(Args a)
    {
        var json = a.Flag("--json");
        var status = vault.Status(a.Positional(0, "status <name> [--json]"));
        Print([status], json, single: true);
        return ExitFor(status);
    }

    int Due(Args a)
    {
        var json = a.Flag("--json");
        var due = vault.Due();
        Print(due, json);
        return due.Count == 0 ? Ok : Stale;
    }

    // ---- getting a value -------------------------------------------------------------------

    int Get(Args a)
    {
        var allowStale = a.Flag("--allow-stale");
        var refresh = a.Flag("--refresh");
        var json = a.Flag("--json");
        var timeout = ParseTimeout(a.Option("--timeout"));
        var name = a.Positional(0, "get <name> [--refresh] [--allow-stale] [--json] [--timeout SECONDS]");

        var status = vault.Status(name);
        if (status.State != SecretState.Fresh && refresh) status = Refresh(name, timeout);

        if (status.State == SecretState.Missing)
            return Fail($"'{status.Definition.Name}' has no value. Run the Keyturn app, or: keyturn set {status.Definition.Name}", Missing);
        if (status.State == SecretState.Stale && !allowStale)
            return Fail($"'{status.Definition.Name}' is stale (expired {status.ExpiresAt:g}). Rotate it, pass --refresh to be prompted, or pass --allow-stale.", Stale);

        var value = vault.Get(name);
        if (value is null) return Fail($"'{name}' has no value.", Missing);

        if (json)
        {
            io.Out.WriteLine(ToJson(new GetDto(status.Definition.Name, status.State.ToString().ToLowerInvariant(), status.ExpiresAt, value)));
        }
        else
        {
            io.Out.Write(value);
            if (!io.OutputRedirected) io.Out.WriteLine(); // a trailing newline only when a human is looking
        }
        return Ok;
    }

    /// <summary>Shows the GUI prompt for one secret, serialised across processes, and returns its new status.</summary>
    SecretStatus Refresh(string name, TimeSpan timeout)
    {
        var def = vault.Find(name) ?? throw new KeyNotFoundException($"No secret named '{name}'.");
        var lockPath = Path.Combine(Path.GetDirectoryName(vault.ConfigPath)!, $"refresh-{def.Name}.lock");

        var deadline = DateTime.UtcNow + timeout;
        using var gate = RefreshLock.TryAcquire(lockPath, timeout);
        if (gate is null)
        {
            io.Err.WriteLine($"keyturn: timed out waiting for another prompt for '{def.Name}'.");
            return vault.Status(name);
        }

        // Whoever held the lock may just have refreshed it.
        var status = vault.Status(name);
        if (status.State == SecretState.Fresh) return status;

        var remaining = deadline - DateTime.UtcNow;
        if (remaining < TimeSpan.FromSeconds(1)) remaining = TimeSpan.FromSeconds(1);

        io.Err.WriteLine($"keyturn: '{def.Name}' needs a new value; opening the prompt...");
        switch (prompt.Launch(def.Name, remaining, out var detail))
        {
            case PromptOutcome.Unavailable: io.Err.WriteLine($"keyturn: {detail}"); break;
            case PromptOutcome.TimedOut: io.Err.WriteLine("keyturn: timed out waiting for the prompt."); break;
        }
        return vault.Status(name);
    }

    // ---- changing state --------------------------------------------------------------------

    int Add(Args a)
    {
        var usage = "add <name> <schedule> [description]";
        var def = vault.Define(a.Positional(0, usage), a.Positional(1, usage), a.Count > 2 ? string.Join(' ', a.Rest(2)) : null);
        io.Err.WriteLine($"Defined '{def.Name}' (rotates {def.Schedule}). Set a value with: keyturn set {def.Name} (Keyturn waits 10 minutes before prompting for a secret that has none yet)");
        WarnIfNotWatching();
        return Ok;
    }

    int Set(Args a)
    {
        var name = a.Positional(0, "set <name>   (value read from stdin)");
        string value;
        if (io.InputRedirected)
        {
            value = io.In.ReadToEnd().TrimEnd('\r', '\n');
        }
        else
        {
            io.Err.Write("Value: ");
            value = io.ReadHidden();
            io.Err.WriteLine();
        }
        vault.Set(name, value);
        io.Err.WriteLine($"Stored '{name}'.");
        return Ok;
    }

    int Remove(Args a) => vault.Remove(a.Positional(0, "remove <name>")) ? Ok : Fail("no such secret.", Usage);

    // keyturn run -s NAME[=ENV_VAR] [-s ...] [--allow-stale] [--refresh] -- command args...
    int RunWith(string[] rest)
    {
        var sep = Array.IndexOf(rest, "--");
        if (sep < 0 || sep == rest.Length - 1)
            return Fail("usage: keyturn run -s NAME[=ENV_VAR] [-s ...] [--allow-stale] [--refresh] [--timeout SECONDS] -- command [args...]", Usage);

        var opts = new Args(rest[..sep]);
        var cmd = rest[(sep + 1)..];
        var allowStale = opts.Flag("--allow-stale");
        var refresh = opts.Flag("--refresh");
        var timeout = ParseTimeout(opts.Option("--timeout"));
        var specs = opts.Options("-s");
        if (specs.Count == 0) return Fail("no secrets selected; use -s NAME[=ENV_VAR].", Usage);

        var psi = new ProcessStartInfo(cmd[0]) { UseShellExecute = false };
        foreach (var arg in cmd[1..]) psi.ArgumentList.Add(arg);

        foreach (var spec in specs)
        {
            var parts = spec.Split('=', 2);
            var status = vault.Status(parts[0]);
            if (status.State != SecretState.Fresh && refresh) status = Refresh(parts[0], timeout);
            if (status.State == SecretState.Missing) return Fail($"'{parts[0]}' has no value.", Missing);
            if (status.State == SecretState.Stale && !allowStale) return Fail($"'{parts[0]}' is stale. Rotate it, pass --refresh, or pass --allow-stale.", Stale);

            var env = parts.Length == 2 ? parts[1] : parts[0].ToUpperInvariant().Replace('-', '_').Replace('.', '_');
            psi.Environment[env] = vault.Get(parts[0]);
        }

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start '{cmd[0]}'.");
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return Fail($"could not run '{cmd[0]}': {ex.Message}", Usage);
        }
    }

    // ---- the background watcher --------------------------------------------------------------------------------------

    string ConfigDirectory => vault.ConfigDirectory;

    void WarnIfNotWatching()
    {
        if (!WatchControl.IsRunning(ConfigDirectory))
            io.Err.WriteLine("keyturn: warning: the watcher isn't running, so nothing will prompt you when a secret expires. Start it with: keyturn watch install");
    }

    int Watch(Args a)
    {
        const string usage = "watch install [--app PATH] [--print] | uninstall | start | stop | status [--json]";
        switch (a.Positional(0, usage))
        {
            case "install": return WatchInstall(a);
            case "uninstall": return WatchUninstall();
            case "start": return WatchStart();
            case "stop": return StopWatcher(report: true);
            case "status": return WatchStatus(a);
            default: return Fail($"usage: keyturn {usage}", Usage);
        }
    }

    bool WaitForWatcher(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!WatchControl.IsRunning(ConfigDirectory))
        {
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(100);
        }
        return true;
    }

    int WatchInstall(Args a)
    {
        var explicitApp = a.Option("--app");
        var print = a.Flag("--print");
        var app = AppLocator.Find(explicitApp)
            ?? throw new ArgumentException(explicitApp is null
                ? "can't find the Keyturn app. Put it next to the keyturn CLI (on macOS: Keyturn.app beside it, or in ~/Applications), or pass --app PATH."
                : $"App not found: {explicitApp}");

        var rc = _autostart.Install(app, print);
        if (print) return rc;
        if (rc != 0) return Fail("couldn't set the watcher up to start at login (see above). You can still run it by hand: Keyturn.App --watch", Usage);

        // Linux and macOS start it as part of installing; Windows needs a nudge.
        if (!WaitForWatcher(TimeSpan.FromMilliseconds(Math.Min(3000, _timeouts.WatcherStart.TotalMilliseconds / 2))))
            _autostart.Start();
        if (WaitForWatcher(_timeouts.WatcherStart))
            io.Err.WriteLine("keyturn: the watcher is running. You'll be prompted as soon as a secret expires.");
        else
            io.Err.WriteLine("keyturn: set up to start at login, but it isn't running yet. Start it now with: keyturn watch start");
        return Ok;
    }

    int WatchStart()
    {
        if (WatchControl.IsRunning(ConfigDirectory))
        {
            io.Err.WriteLine("keyturn: the watcher is already running.");
            return Ok;
        }
        if (!_autostart.IsInstalled())
            return Fail("the watcher isn't set up to start at login. Run: keyturn watch install", Usage);
        if (_autostart.Start() != 0) return Fail("couldn't start the watcher (see above)", Usage);
        if (!WaitForWatcher(_timeouts.WatcherStart))
            return Fail("started it, but the watcher isn't running yet. Check: keyturn watch status", Usage);
        io.Err.WriteLine("keyturn: the watcher is running.");
        return Ok;
    }

    /// <summary>Asks the watcher to stop through its stop file, and only kills it (never anything but a Keyturn process) if it ignores that.</summary>
    int StopWatcher(bool report)
    {
        if (!WatchControl.IsRunning(ConfigDirectory))
        {
            if (report) io.Err.WriteLine("keyturn: the watcher isn't running.");
            return Ok;
        }

        var info = WatchControl.Info(ConfigDirectory);
        WatchControl.RequestStop(ConfigDirectory);
        var stopped = WatchControl.WaitUntilStopped(ConfigDirectory, _timeouts.WatcherStop);
        WatchControl.ClearStopRequest(ConfigDirectory);
        if (!stopped && info is not null && WatchControl.TryKill(info))
            stopped = WatchControl.WaitUntilStopped(ConfigDirectory, TimeSpan.FromSeconds(5));

        if (!stopped) return Fail("couldn't stop the watcher", Usage);
        if (report) io.Err.WriteLine("keyturn: stopped the watcher. It starts again at your next login; start it now with: keyturn watch start");
        return Ok;
    }

    int WatchUninstall()
    {
        if (StopWatcher(report: false) != Ok) io.Err.WriteLine("keyturn: warning: the watcher couldn't be stopped; removing it from your login items anyway.");
        _autostart.Uninstall();
        io.Err.WriteLine("keyturn: removed the watcher from your login items.");
        return Ok;
    }

    int WatchStatus(Args a)
    {
        var json = a.Flag("--json");
        var info = WatchControl.Info(ConfigDirectory);
        var running = WatchControl.IsRunning(ConfigDirectory);
        var installed = _autostart.IsInstalled();
        var next = vault.NextToExpire();
        var store = new SnoozeStore(ConfigDirectory, _now);
        var snoozed = vault.List()
            .Select(d => (d.Name, Until: store.SnoozedUntil(d)))
            .Where(x => x.Until is not null)
            .Select(x => (x.Name, Until: x.Until!.Value))
            .OrderBy(x => x.Until)
            .ToList();
        var log = new WatchLog(ConfigDirectory);

        if (json)
        {
            io.Out.WriteLine(ToJson(new WatchStatusDto(
                installed, running, info?.ProcessId, info?.StartedAt,
                next is { ExpiresAt: { } at } ? new NextExpiryDto(next.Definition.Name, at) : null,
                snoozed.Select(x => new SnoozedDto(x.Name, x.Until)).ToList(),
                WatchFiles.Log(ConfigDirectory))));
        }
        else
        {
            io.Out.WriteLine($"watcher:   {(running ? $"running{(info is null ? "" : $" (pid {info.ProcessId}, since {info.StartedAt:g})")}" : "not running")}");
            io.Out.WriteLine($"autostart: {(installed ? "set up to start at login" : "not set up")}");
            io.Out.WriteLine($"next:      {(next is { ExpiresAt: { } expires } ? $"{next.Definition.Name} expires {expires:g} (in {Humanize.Span(expires - _now())})" : "nothing scheduled to expire")}");
            foreach (var (name, until) in snoozed) io.Out.WriteLine($"snoozed:   {name} until {until:t}");
            io.Out.WriteLine($"log:       {WatchFiles.Log(ConfigDirectory)}");
            foreach (var line in log.Tail(5)) io.Out.WriteLine($"             {line}");
            if (!running) io.Out.WriteLine(installed ? "Start it with: keyturn watch start" : "Set it up with: keyturn watch install");
        }
        return running ? Ok : NotRunning;
    }

    // ---- output ----------------------------------------------------------------------------

    static SecretDto ToDto(SecretStatus s) => new(
        s.Definition.Name, s.Definition.Schedule, s.Definition.Description,
        s.State.ToString().ToLowerInvariant(), s.Definition.LastSetAt, s.ExpiresAt);

    /// <summary>Compact when piped (one line for scripts), indented on a terminal.</summary>
    string ToJson<T>(T value) =>
        JsonSerializer.Serialize(value, (JsonTypeInfo<T>)(io.OutputRedirected ? (JsonSerializerContext)CompactJson.Default : IndentedJson.Default).GetTypeInfo(typeof(T))!);

    void Print(IReadOnlyList<SecretStatus> statuses, bool json, bool single = false)
    {
        if (json)
        {
            var dtos = statuses.Select(ToDto).ToList();
            io.Out.WriteLine(single ? ToJson(dtos[0]) : ToJson(dtos));
            return;
        }
        foreach (var s in statuses)
            io.Out.WriteLine($"{s.Definition.Name,-24} {s.State.ToString().ToLowerInvariant(),-8} {s.Definition.Schedule,-12} {Humanize.Status(s, _now())}");
    }

    static string VersionText()
    {
        var info = typeof(KeyturnCli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = info.IndexOf('+');
        return plus < 0 ? info : info[..plus]; // drop the source-link commit suffix
    }

    static int ExitFor(SecretStatus s) => s.State switch { SecretState.Missing => Missing, SecretState.Stale => Stale, _ => Ok };

    static TimeSpan ParseTimeout(string? seconds) =>
        seconds is null ? DefaultRefreshTimeout
        : double.TryParse(seconds, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : throw new ArgumentException("--timeout must be a positive number of seconds.");

    int Fail(string message, int code, bool printUsage = false)
    {
        io.Err.WriteLine($"keyturn: {message}");
        return printUsage ? PrintUsage(code) : code;
    }

    int PrintUsage(int code)
    {
        io.Err.WriteLine("""
            keyturn - rotating secrets in your OS credential store

              list [--json]                     all secrets and their state
              status <name> [--json]            one secret
              due [--json]                      secrets needing a new value
              add <name> <schedule> [desc]      define a secret; schedule: 12h, 7d, 30m, daily@06:00
              set <name>                        store a value (prompt, or read from stdin)
              get <name> [options]              print the value
                  --refresh                     if stale/missing, show the prompt and wait
                  --timeout SECONDS             how long --refresh waits (default 300)
                  --allow-stale                 return an expired value instead of failing
                  --json                        {name,state,expiresAt,value}
              remove <name>
              run -s NAME[=ENV] ... [--refresh] [--allow-stale] -- cmd ...
                                                run cmd with secrets in its environment
              watch install [--app PATH]        start the background watcher at login and now (--print to preview)
              watch uninstall                   stop it and remove it from your login items
              watch start | stop                start or stop it now
              watch status [--json]             is it running, what expires next, what is snoozed (exit 2 if not running)
              --version

            exit codes: 0 ok, 1 usage/unknown secret, 2 no value (watch status: not running), 3 stale, 4 storage error
            """);
        return code;
    }
}
