using System.Diagnostics;
using System.Text.Json;

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
/// <item>Exit codes: 0 ok, 1 usage error or unknown secret, 2 no value, 3 stale, 4 credential store error.</item>
/// <item><c>get</c> never prompts on the terminal; only <c>--refresh</c> may show the GUI prompt.</item>
/// </list>
/// </summary>
public sealed class KeyturnCli(SecretVault vault, CliIo io, IPromptLauncher prompt, Func<DateTimeOffset>? now = null)
{
    public const int Ok = 0, Usage = 1, Missing = 2, Stale = 3, StoreError = 4;
    public static readonly TimeSpan DefaultRefreshTimeout = TimeSpan.FromMinutes(5);

    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

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
            // Credential store failures (locked keyring, no D-Bus session, ...) land here.
            var detail = ex.InnerException is { } inner ? $" ({inner.Message})" : "";
            return Fail($"credential store error: {ex.Message.Split('\n')[0]}{detail}", StoreError);
        }
    }

    int Dispatch(string[] argv)
    {
        if (argv.Length == 0 || argv[0] is "-h" or "--help" or "help") return PrintUsage(argv.Length == 0 ? Usage : Ok);
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
            "schedule" => ScheduleCommand(args),
            _ => Fail($"unknown command '{command}'", Usage, printUsage: true),
        };
    }

    // ---- reading state ---------------------------------------------------------------------

    int List(Args a)
    {
        var json = a.Flag("--json");
        Print(vault.Statuses(), json);
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
            var dto = ToDto(status);
            io.Out.WriteLine(JsonSerializer.Serialize(new { dto.Name, dto.State, dto.ExpiresAt, Value = value }, Json(io.OutputRedirected)));
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
        io.Err.WriteLine($"Defined '{def.Name}' (rotates {def.Schedule}). Set a value with: keyturn set {def.Name}");
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

    int ScheduleCommand(Args a)
    {
        switch (a.Positional(0, "schedule install [--app PATH] [--print] | uninstall"))
        {
            case "install":
                var app = a.Option("--app") ?? Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Keyturn.App.exe" : "Keyturn.App");
                return Scheduler.Install(app, a.Flag("--print"));
            case "uninstall":
                return Scheduler.Uninstall();
            default:
                return Fail("usage: keyturn schedule install [--app PATH] [--print] | uninstall", Usage);
        }
    }

    // ---- output ----------------------------------------------------------------------------

    sealed record SecretDto(string Name, string Schedule, string? Description, string State, DateTimeOffset? LastSetAt, DateTimeOffset? ExpiresAt);

    static SecretDto ToDto(SecretStatus s) => new(
        s.Definition.Name, s.Definition.Schedule, s.Definition.Description,
        s.State.ToString().ToLowerInvariant(), s.Definition.LastSetAt, s.ExpiresAt);

    static JsonSerializerOptions Json(bool compact) => new(JsonSerializerDefaults.Web) { WriteIndented = !compact };

    void Print(IReadOnlyList<SecretStatus> statuses, bool json, bool single = false)
    {
        if (json)
        {
            var dtos = statuses.Select(ToDto).ToList();
            io.Out.WriteLine(single ? JsonSerializer.Serialize(dtos[0], Json(io.OutputRedirected)) : JsonSerializer.Serialize(dtos, Json(io.OutputRedirected)));
            return;
        }
        foreach (var s in statuses)
            io.Out.WriteLine($"{s.Definition.Name,-24} {s.State.ToString().ToLowerInvariant(),-8} {s.Definition.Schedule,-12} {Humanize.Status(s, _now())}");
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
              schedule install [--app PATH]     launch the prompt at login and hourly (--print to preview)
              schedule uninstall

            exit codes: 0 ok, 1 usage/unknown secret, 2 no value, 3 stale, 4 credential store error
            """);
        return code;
    }
}
