using System.Text.Json;
using Keyturn.Cli;

namespace Keyturn.Tests;

/// <summary>Pins the CLI behaviour other tools rely on: streams, exit codes, JSON shapes, no surprise prompts.</summary>
public sealed class CliContractTests : IDisposable
{
    sealed class MemoryBackend : ISecretBackend
    {
        public readonly Dictionary<string, string> Values = new();
        public bool Fail;
        public string? Get(string name) => Fail ? throw new IOException("keyring locked") : Values.GetValueOrDefault(name);
        public void Set(string name, string value) => Values[name] = value;
        public void Remove(string name) => Values.Remove(name);
    }

    sealed class FakePrompt : IPromptLauncher
    {
        public readonly List<string> Calls = new();
        public Action<string>? OnLaunch;
        public PromptOutcome Outcome = PromptOutcome.Completed;
        public string? Detail;

        public PromptOutcome Launch(string secretName, TimeSpan timeout, out string? detail)
        {
            Calls.Add(secretName);
            OnLaunch?.Invoke(secretName);
            detail = Detail;
            return Outcome;
        }
    }

    sealed class Forbidden : TextReader
    {
        public override string? ReadLine() => throw new InvalidOperationException("CLI read stdin when it must not");
        public override string ReadToEnd() => throw new InvalidOperationException("CLI read stdin when it must not");
        public override int Read() => throw new InvalidOperationException("CLI read stdin when it must not");
    }

    readonly string _dir = Directory.CreateTempSubdirectory("keyturn-cli").FullName;
    readonly MemoryBackend _backend = new();
    readonly FakePrompt _prompt = new();
    DateTimeOffset _now = DateTimeOffset.Parse("2026-01-01T10:00:00+00:00");

    SecretVault Vault() => new(_backend, Path.Combine(_dir, "secrets.json"), () => _now);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    record Result(int Code, string Out, string Err);

    Result Run(string[] args, string? stdin = null, bool outputRedirected = true)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var io = new CliIo(o, e, stdin is null ? new Forbidden() : new StringReader(stdin), stdin is not null, outputRedirected,
            () => throw new InvalidOperationException("CLI prompted on the terminal when it must not"));
        var code = new KeyturnCli(Vault(), io, _prompt, () => _now).Run(args);
        return new Result(code, o.ToString(), e.ToString());
    }

    void Seed(string name, string schedule, string? value, string? at = null)
    {
        var v = Vault();
        v.Define(name, schedule);
        if (value is not null) v.Set(name, value);
        _ = at;
    }

    void MakeStale(string name, string value = "old")
    {
        Seed(name, "1h", value);
        _now = _now.AddHours(2);
    }

    // ---- get: stdout is data only ----------------------------------------------------------

    [Fact]
    public void GetPrintsExactlyTheValueWhenPiped()
    {
        Seed("vpn", "1d", "p@ss w0rd");
        var r = Run(["get", "vpn"]);
        Assert.Equal((0, "p@ss w0rd", ""), (r.Code, r.Out, r.Err));
    }

    [Fact]
    public void GetAddsANewlineOnlyOnATerminal()
    {
        Seed("vpn", "1d", "pw");
        Assert.Equal("pw" + Environment.NewLine, Run(["get", "vpn"], outputRedirected: false).Out);
    }

    [Fact]
    public void GetMissingExits2WithEmptyStdout()
    {
        Seed("vpn", "1d", null);
        var r = Run(["get", "vpn"]);
        Assert.Equal(2, r.Code);
        Assert.Equal("", r.Out);
        Assert.Contains("vpn", r.Err);
    }

    [Fact]
    public void GetStaleExits3WithEmptyStdoutUnlessAllowed()
    {
        MakeStale("vpn");
        var r = Run(["get", "vpn"]);
        Assert.Equal(3, r.Code);
        Assert.Equal("", r.Out);

        var allowed = Run(["get", "vpn", "--allow-stale"]);
        Assert.Equal((0, "old"), (allowed.Code, allowed.Out));
    }

    [Fact]
    public void GetUnknownSecretExits1()
    {
        var r = Run(["get", "nope"]);
        Assert.Equal(1, r.Code);
        Assert.Equal("", r.Out);
    }

    [Fact]
    public void GetWithoutNameIsAUsageError() => Assert.Equal(1, Run(["get"]).Code);

    [Fact]
    public void GetNeverTouchesStdinOrTheTerminal()
    {
        // Run() installs readers that throw; reaching this assert means none was used.
        MakeStale("vpn");
        Assert.Equal(3, Run(["get", "vpn"]).Code);
        Assert.Equal(1, Run(["get", "nope"]).Code);
    }

    [Fact]
    public void StoreFailureExits4()
    {
        Seed("vpn", "1d", "pw");
        _backend.Fail = true;
        var r = Run(["get", "vpn"]);
        Assert.Equal(4, r.Code);
        Assert.Equal("", r.Out);
        Assert.Contains("storage error", r.Err);
    }

    // ---- --refresh -------------------------------------------------------------------------

    [Fact]
    public void RefreshOnFreshSecretNeverPrompts()
    {
        Seed("vpn", "1d", "pw");
        var r = Run(["get", "vpn", "--refresh"]);
        Assert.Equal((0, "pw"), (r.Code, r.Out));
        Assert.Empty(_prompt.Calls);
    }

    [Fact]
    public void RefreshPromptsOnceAndReturnsTheNewValue()
    {
        MakeStale("vpn");
        _prompt.OnLaunch = name => Vault().Set(name, "new-value");
        var r = Run(["get", "vpn", "--refresh"]);
        Assert.Equal((0, "new-value"), (r.Code, r.Out));
        Assert.Equal(["vpn"], _prompt.Calls);
        Assert.Contains("opening the prompt", r.Err); // diagnostics stay on stderr
    }

    [Fact]
    public void RefreshOnMissingSecretPrompts()
    {
        Seed("vpn", "1d", null);
        _prompt.OnLaunch = name => Vault().Set(name, "first");
        Assert.Equal("first", Run(["get", "vpn", "--refresh"]).Out);
    }

    [Fact]
    public void RefreshDismissedKeepsTheStaleExitCode()
    {
        MakeStale("vpn");
        var r = Run(["get", "vpn", "--refresh"]); // prompt "completes" without saving
        Assert.Equal(3, r.Code);
        Assert.Equal("", r.Out);
    }

    [Fact]
    public void RefreshDismissedWithAllowStaleReturnsTheOldValue()
    {
        MakeStale("vpn");
        var r = Run(["get", "vpn", "--refresh", "--allow-stale"]);
        Assert.Equal((0, "old"), (r.Code, r.Out));
    }

    [Fact]
    public void RefreshWithoutAGraphicalSessionExplainsAndKeepsTheStateCode()
    {
        Seed("vpn", "1d", null);
        _prompt.Outcome = PromptOutcome.Unavailable;
        _prompt.Detail = "no graphical session";
        var r = Run(["get", "vpn", "--refresh"]);
        Assert.Equal(2, r.Code);
        Assert.Equal("", r.Out);
        Assert.Contains("no graphical session", r.Err);
    }

    [Fact]
    public void RefreshTimeoutKeepsTheStateCode()
    {
        MakeStale("vpn");
        _prompt.Outcome = PromptOutcome.TimedOut;
        var r = Run(["get", "vpn", "--refresh", "--timeout", "1"]);
        Assert.Equal(3, r.Code);
        Assert.Contains("timed out", r.Err);
    }

    [Fact]
    public async Task ConcurrentRefreshesShareOnePrompt()
    {
        MakeStale("vpn");
        var lockPath = Path.Combine(_dir, "refresh-vpn.lock");
        var other = RefreshLock.TryAcquire(lockPath, TimeSpan.Zero)!; // another tool is already prompting

        var release = Task.Run(() =>
        {
            Thread.Sleep(400);
            Vault().Set("vpn", "set-by-the-other-prompt"); // the user answered that prompt
            other.Dispose();
        });

        var r = Run(["get", "vpn", "--refresh", "--timeout", "10"]);
        await release;

        Assert.Equal((0, "set-by-the-other-prompt"), (r.Code, r.Out));
        Assert.Empty(_prompt.Calls); // we waited instead of opening a second window
    }

    [Fact]
    public void WaitingForAnotherPromptGivesUpAtTheTimeout()
    {
        MakeStale("vpn");
        using var other = RefreshLock.TryAcquire(Path.Combine(_dir, "refresh-vpn.lock"), TimeSpan.Zero)!;
        var r = Run(["get", "vpn", "--refresh", "--timeout", "0.4"]);
        Assert.Equal(3, r.Code);
        Assert.Empty(_prompt.Calls);
        Assert.Contains("timed out waiting", r.Err);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("soon")]
    public void BadTimeoutIsAUsageError(string timeout)
    {
        MakeStale("vpn");
        Assert.Equal(1, Run(["get", "vpn", "--refresh", "--timeout", timeout]).Code);
    }

    // ---- JSON ------------------------------------------------------------------------------

    [Fact]
    public void ListJsonIsAnArrayWithAStableShapeAndNoValues()
    {
        Seed("vpn", "daily@06:00", "super-secret");
        Seed("db", "12h", null);
        var r = Run(["list", "--json"]);
        Assert.Equal(0, r.Code);
        Assert.DoesNotContain("super-secret", r.Out);

        using var doc = JsonDocument.Parse(r.Out);
        var items = doc.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!);
        Assert.Equal("fresh", items["vpn"].GetProperty("state").GetString());
        Assert.Equal("daily@06:00", items["vpn"].GetProperty("schedule").GetString());
        Assert.NotEqual(JsonValueKind.Null, items["vpn"].GetProperty("expiresAt").ValueKind);
        Assert.Equal("missing", items["db"].GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, items["db"].GetProperty("expiresAt").ValueKind);
        foreach (var key in new[] { "name", "schedule", "description", "state", "lastSetAt", "expiresAt" })
            Assert.True(items["vpn"].TryGetProperty(key, out _), $"missing key {key}");
    }

    [Fact]
    public void StatusJsonIsAnObjectAndKeepsTheExitCode()
    {
        MakeStale("vpn");
        var r = Run(["status", "vpn", "--json"]);
        Assert.Equal(3, r.Code);
        using var doc = JsonDocument.Parse(r.Out);
        Assert.Equal("stale", doc.RootElement.GetProperty("state").GetString());
    }

    [Fact]
    public void DueJsonIsEmptyArrayWithExit0WhenNothingIsDue()
    {
        Seed("vpn", "1d", "pw");
        var r = Run(["due", "--json"]);
        Assert.Equal(0, r.Code);
        Assert.Equal("[]", r.Out.Trim());
    }

    [Fact]
    public void DueExits3WhenSomethingIsDue()
    {
        Seed("vpn", "1d", null);
        Assert.Equal(3, Run(["due", "--json"]).Code);
    }

    [Fact]
    public void GetJsonCarriesTheValue()
    {
        Seed("vpn", "1d", "pw");
        using var doc = JsonDocument.Parse(Run(["get", "vpn", "--json"]).Out);
        Assert.Equal("pw", doc.RootElement.GetProperty("value").GetString());
        Assert.Equal("fresh", doc.RootElement.GetProperty("state").GetString());
    }

    // ---- writing ---------------------------------------------------------------------------

    [Fact]
    public void SetReadsStdinTrimsOneNewlineAndKeepsStdoutClean()
    {
        Seed("vpn", "1d", null);
        var r = Run(["set", "vpn"], stdin: "hunter2\n");
        Assert.Equal(0, r.Code);
        Assert.Equal("", r.Out);
        Assert.Equal("hunter2", Vault().Get("vpn"));
    }

    [Fact]
    public void AddAndRemoveKeepStdoutClean()
    {
        var add = Run(["add", "vpn", "12h", "Corporate", "VPN"]);
        Assert.Equal((0, ""), (add.Code, add.Out));
        Assert.Equal("Corporate VPN", Vault().Find("vpn")!.Description);
        Assert.Equal((0, ""), (Run(["remove", "vpn"]).Code, Run(["list"]).Out));
    }

    [Fact]
    public void EmptyStdinIsRejected()
    {
        Seed("vpn", "1d", null);
        Assert.Equal(1, Run(["set", "vpn"], stdin: "").Code);
    }

    // ---- run -------------------------------------------------------------------------------

    [Fact]
    public void RunRefusesStaleAndMissingSecretsWithoutStartingTheCommand()
    {
        MakeStale("vpn");
        Assert.Equal(3, Run(["run", "-s", "vpn", "--", "definitely-not-a-real-command"]).Code);

        Seed("db", "1d", null);
        Assert.Equal(2, Run(["run", "-s", "db", "--", "definitely-not-a-real-command"]).Code);
    }

    [Fact]
    public void RunInjectsTheSecretIntoTheChildEnvironment()
    {
        if (OperatingSystem.IsWindows()) return; // relies on printenv
        Seed("vpn-pass", "1d", "pw");
        // printenv exits 0 only if the variable is set; the default name is derived from the secret name.
        Assert.Equal(0, Run(["run", "-s", "vpn-pass", "--", "printenv", "VPN_PASS"]).Code);
        Assert.Equal(0, Run(["run", "-s", "vpn-pass=CUSTOM", "--", "printenv", "CUSTOM"]).Code);
    }

    [Fact]
    public void RunPropagatesTheChildExitCode()
    {
        if (OperatingSystem.IsWindows()) return;
        Seed("vpn", "1d", "pw");
        Assert.Equal(1, Run(["run", "-s", "vpn", "--", "printenv", "SOME_UNSET_VARIABLE_XYZ"]).Code);
    }

    [Fact]
    public void RunWithoutSecretsOrSeparatorIsAUsageError()
    {
        Assert.Equal(1, Run(["run", "--", "true"]).Code);
        Assert.Equal(1, Run(["run", "-s", "vpn", "true"]).Code);
    }

    [Fact]
    public void UnknownCommandIsAUsageError() => Assert.Equal(1, Run(["frobnicate"]).Code);
}

public class CliVersionTests
{
    [Fact]
    public void VersionPrintsToStdoutAndExits0()
    {
        var o = new StringWriter();
        var io = new CliIo(o, new StringWriter(), TextReader.Null, true, true, () => "");
        var code = new KeyturnCli(new SecretVault(new NullBackend(), Path.Combine(Path.GetTempPath(), "nope", "s.json")), io, new ProcessPromptLauncher()).Run(["--version"]);
        Assert.Equal(0, code);
        Assert.Matches(@"^\d+\.\d+\.\d+", o.ToString().Trim());
    }

    sealed class NullBackend : ISecretBackend
    {
        public string? Get(string name) => null;
        public void Set(string name, string value) { }
        public void Remove(string name) { }
    }
}
