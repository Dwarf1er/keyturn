using Keyturn;

namespace Keyturn.Tests;

/// <summary>The background watcher, driven by a fake clock so hours of waiting take no time.</summary>
public sealed class WatcherTests : IDisposable
{
    sealed class MemoryBackend : ISecretBackend
    {
        public readonly Dictionary<string, string> Values = new();
        public bool Fail;
        public int Reads;
        public string? Get(string name) { Reads++; return Fail ? throw new IOException("keyring locked") : Values.GetValueOrDefault(name); }
        public void Set(string name, string value) => Values[name] = value;
        public void Remove(string name) => Values.Remove(name);
    }

    /// <summary>Sleeping just moves the clock forward. Hooks run when it passes a given moment, to simulate things happening meanwhile.</summary>
    sealed class FakeClock(DateTimeOffset start)
    {
        public DateTimeOffset Now = start;
        public TimeSpan? JumpNextDelayBy;
        readonly List<(DateTimeOffset At, Action Do, bool[] Done)> _hooks = [];

        public void At(DateTimeOffset moment, Action action) => _hooks.Add((moment, action, [false]));

        public Task Delay(TimeSpan wait, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Now += JumpNextDelayBy ?? wait; // a suspended laptop wakes up much later than it asked to
            JumpNextDelayBy = null;
            foreach (var hook in _hooks.Where(h => !h.Done[0] && Now >= h.At).ToList()) { hook.Done[0] = true; hook.Do(); }
            return Task.CompletedTask;
        }
    }

    /// <summary>Stands in for the prompt window: records when it opened, then plays the user's reaction.</summary>
    sealed class FakePrompt(FakeClock clock) : IPromptRunner
    {
        public readonly List<DateTimeOffset> Opened = new();
        public Func<int> OnOpen = () => 0;   // by default the user dismisses it ("Later") without saving
        public Task<int> RunAsync(CancellationToken cancellationToken)
        {
            Opened.Add(clock.Now);
            return Task.FromResult(OnOpen());
        }
    }

    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T10:00:00+00:00");

    readonly string _dir = Directory.CreateTempSubdirectory("keyturn-watch").FullName;
    readonly MemoryBackend _backend = new();
    readonly FakeClock _clock = new(T0);
    readonly FakePrompt _prompt;
    readonly SecretVault _vault;
    readonly SnoozeStore _snooze;
    readonly List<string> _log = new();

    public WatcherTests()
    {
        _prompt = new FakePrompt(_clock);
        _vault = new SecretVault(_backend, Path.Combine(_dir, "secrets.json"), () => _clock.Now);
        _snooze = new SnoozeStore(_dir, () => _clock.Now);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Runs the watcher until the fake clock reaches <paramref name="until"/>.</summary>
    async Task RunUntil(TimeSpan duration, WatcherOptions? options = null, Func<bool>? alsoStopWhen = null)
    {
        var end = _clock.Now + duration;
        var watcher = new Watcher(_vault, _snooze, _prompt, options, () => _clock.Now, _clock.Delay,
            () => _clock.Now >= end || (alsoStopWhen?.Invoke() ?? false), _log.Add);
        await watcher.RunAsync(CancellationToken.None);
    }

    void UserSaves() { foreach (var s in _vault.Expired()) _vault.Set(s.Definition.Name, "new-value"); }

    /// <summary>A secret that has been without a value for a long time, so the new-secret grace period is long over.</summary>
    void DefineNeverSet(string name, string schedule)
    {
        var now = _clock.Now;
        _clock.Now = T0.AddHours(-1);
        _vault.Define(name, schedule);
        _clock.Now = now;
    }

    [Fact]
    public async Task PromptsExactlyWhenTheSecretExpiresAndNotBefore()
    {
        _vault.Define("vpn", "1h"); _vault.Set("vpn", "pw");
        _prompt.OnOpen = () => { UserSaves(); return 0; };
        await RunUntil(TimeSpan.FromHours(1.5));
        Assert.Equal([T0.AddHours(1)], _prompt.Opened);
    }

    [Fact]
    public async Task FreshSecretsAreLeftAlone()
    {
        _vault.Define("vpn", "1d"); _vault.Set("vpn", "pw");
        await RunUntil(TimeSpan.FromHours(6));
        Assert.Empty(_prompt.Opened);
    }

    [Fact]
    public async Task ASecretLeftWithoutAValueForLongPromptsImmediately()
    {
        DefineNeverSet("vpn", "1d");
        _prompt.OnOpen = () => { UserSaves(); return 0; };
        await RunUntil(TimeSpan.FromHours(1));
        Assert.Equal([T0], _prompt.Opened);
    }

    [Fact]
    public async Task ANewSecretWithoutAValueIsLeftAloneForTenMinutesThenPrompted()
    {
        _vault.Define("vpn", "1d"); // just created, no value yet
        _prompt.OnOpen = () => { UserSaves(); return 0; };
        await RunUntil(TimeSpan.FromHours(1));
        Assert.Equal([T0.AddMinutes(10)], _prompt.Opened);
    }

    [Fact]
    public async Task SettingTheValueRightAfterCreatingItNeverPrompts()
    {
        // `keyturn add vpn 1d` then a minute later `keyturn set vpn`: exactly the flow that used to pop up in the way.
        _vault.Define("vpn", "1d");
        _clock.At(T0.AddMinutes(1), () => _vault.Set("vpn", "pw"));
        await RunUntil(TimeSpan.FromHours(2));
        Assert.Empty(_prompt.Opened);
    }

    [Fact]
    public async Task DismissedSecretIsLeftAloneForTenMinutesThenAskedAgain()
    {
        DefineNeverSet("vpn", "1d"); // no value, so due at once
        await RunUntil(TimeSpan.FromHours(1), alsoStopWhen: () => _prompt.Opened.Count >= 3);
        Assert.Equal([T0, T0.AddMinutes(10), T0.AddMinutes(20)], _prompt.Opened);
    }

    [Fact]
    public async Task SavingMeansNoSnoozeAndTheNextExpiryPromptsAgain()
    {
        DefineNeverSet("vpn", "1h");
        _prompt.OnOpen = () => { UserSaves(); return 0; };
        await RunUntil(TimeSpan.FromHours(2.5));
        Assert.Equal([T0, T0.AddHours(1), T0.AddHours(2)], _prompt.Opened);
        Assert.Empty(_snooze.Active());
    }

    [Fact]
    public async Task ARotationVoidsTheSnoozeSoTheNextExpiryIsNeverMissed()
    {
        _vault.Define("vpn", "1m"); _vault.Set("vpn", "pw");
        // Dismissed at +1m (snoozed until +11m); the user rotates it at +3m, so it expires again at +4m.
        _clock.At(T0.AddMinutes(3), () => _vault.Set("vpn", "rotated"));
        await RunUntil(TimeSpan.FromMinutes(6), alsoStopWhen: () => _prompt.Opened.Count >= 2);
        Assert.Equal([T0.AddMinutes(1), T0.AddMinutes(4)], _prompt.Opened);
    }

    [Fact]
    public async Task APromptThatFailsToOpenIsRetriedAfterThirtySeconds()
    {
        DefineNeverSet("vpn", "1d");
        _prompt.OnOpen = () => 1; // e.g. no display yet at login
        await RunUntil(TimeSpan.FromMinutes(5), alsoStopWhen: () => _prompt.Opened.Count >= 3);
        Assert.Equal([T0, T0.AddSeconds(30), T0.AddSeconds(60)], _prompt.Opened);
    }

    [Fact]
    public async Task APromptRunnerThatThrowsIsTreatedAsAFailureNotACrash()
    {
        DefineNeverSet("vpn", "1d");
        var thrower = new ThrowingPrompt();
        var watcher = new Watcher(_vault, _snooze, thrower, null, () => _clock.Now, _clock.Delay, () => thrower.Calls >= 2, _log.Add);
        await watcher.RunAsync(CancellationToken.None);
        Assert.Equal(2, thrower.Calls);
        Assert.Contains(_log, l => l.Contains("could not open the prompt"));
    }

    sealed class ThrowingPrompt : IPromptRunner
    {
        public int Calls;
        public Task<int> RunAsync(CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("no display"); }
    }

    [Fact]
    public async Task ASecretBeingRefreshedByAToolIsNotPromptedTwice()
    {
        DefineNeverSet("vpn", "1d"); // due
        using var toolsWindow = RefreshLock.TryAcquire(_vault.RefreshLockPath("vpn"), TimeSpan.Zero)!; // `keyturn get --refresh` is asking
        await RunUntil(TimeSpan.FromHours(1));
        Assert.Empty(_prompt.Opened);
    }

    [Fact]
    public async Task OnlyTheSecretsItIsNotBusyWithAreSnoozed()
    {
        DefineNeverSet("a", "1d"); DefineNeverSet("b", "1d");
        using var toolsWindow = RefreshLock.TryAcquire(_vault.RefreshLockPath("b"), TimeSpan.Zero)!;
        await RunUntil(TimeSpan.FromMinutes(1), alsoStopWhen: () => _prompt.Opened.Count >= 1);
        Assert.False(_snooze.IsSnoozed(_vault.Find("b")!));   // b belongs to the tool's window
        Assert.True(_snooze.IsSnoozed(_vault.Find("a")!));    // a was ours and was dismissed
    }

    [Fact]
    public async Task ASecretDefinedWhileTheWatcherSleepsIsNoticed()
    {
        _clock.At(T0.AddMinutes(2), () => { _vault.Define("new", "1m"); _vault.Set("new", "pw"); });
        _prompt.OnOpen = () => { UserSaves(); return 0; };
        await RunUntil(TimeSpan.FromMinutes(10), alsoStopWhen: () => _prompt.Opened.Count >= 1);
        Assert.Equal([T0.AddMinutes(3)], _prompt.Opened); // set at +2m with a 1m interval
    }

    [Fact]
    public async Task ASuspendedMachinePromptsAsSoonAsItWakes()
    {
        _vault.Define("vpn", "1h"); _vault.Set("vpn", "pw");
        _clock.JumpNextDelayBy = TimeSpan.FromHours(3); // laptop lid closed for three hours
        _prompt.OnOpen = () => { UserSaves(); return 0; };
        await RunUntil(TimeSpan.FromHours(4), alsoStopWhen: () => _prompt.Opened.Count >= 1);
        Assert.Equal([T0.AddHours(3)], _prompt.Opened);
    }

    [Fact]
    public async Task ADamagedSecretsFileDoesNotKillTheWatcherAndItRecovers()
    {
        DefineNeverSet("vpn", "1d");
        var path = Path.Combine(_dir, "secrets.json");
        var good = File.ReadAllText(path);
        File.WriteAllText(path, "{ this is not json");
        _clock.At(T0.AddMinutes(1), () => File.WriteAllText(path, good));
        await RunUntil(TimeSpan.FromMinutes(5), alsoStopWhen: () => _prompt.Opened.Count >= 1);
        Assert.Contains(_log, l => l.StartsWith("error:"));
        Assert.Single(_prompt.Opened);
        Assert.True(_prompt.Opened[0] >= T0.AddMinutes(1));
    }

    [Fact]
    public async Task ALockedKeyringFallsBackToCheckingSchedules()
    {
        // A secret that has a value makes the vault consult the keyring; make it stale and lock the keyring (e.g. at login).
        _vault.Define("vpn", "1h"); _vault.Set("vpn", "pw");
        _clock.Now = T0.AddHours(2);
        _backend.Fail = true;
        await RunUntil(TimeSpan.FromMinutes(1), alsoStopWhen: () => _prompt.Opened.Count >= 1);
        Assert.Single(_prompt.Opened);
        Assert.Contains(_log, l => l.Contains("credential store unavailable"));
    }

    [Fact]
    public async Task ItDoesNotReadTheKeyringOnEveryWake()
    {
        _vault.Define("vpn", "1d"); _vault.Set("vpn", "pw");
        _backend.Reads = 0;
        await RunUntil(TimeSpan.FromMinutes(10)); // ~300 wakes of 2s, but only the first (and the 15-minute) check reads the store
        Assert.True(_backend.Reads <= 2, $"read the keyring {_backend.Reads} times");
    }

    [Fact]
    public async Task AStopRequestAndCancellationBothEndTheLoop()
    {
        var watcher = new Watcher(_vault, _snooze, _prompt, null, () => _clock.Now, _clock.Delay, () => true, _log.Add);
        await watcher.RunAsync(CancellationToken.None); // returns at once

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await new Watcher(_vault, _snooze, _prompt, null, () => _clock.Now, _clock.Delay, null, _log.Add).RunAsync(cts.Token);
        Assert.Contains(_log, l => l == "watcher stopped");
    }

    [Fact]
    public async Task ItNeverBusyLoops()
    {
        // With nothing scheduled the watcher must still sleep (in slices), not spin.
        var delays = new List<TimeSpan>();
        var end = T0.AddMinutes(1);
        var watcher = new Watcher(_vault, _snooze, _prompt, null, () => _clock.Now,
            (d, t) => { delays.Add(d); return _clock.Delay(d, t); }, () => _clock.Now >= end, _log.Add);
        await watcher.RunAsync(CancellationToken.None);
        Assert.All(delays, d => Assert.InRange(d, TimeSpan.FromMilliseconds(50), TimeSpan.FromSeconds(2)));
        Assert.InRange(delays.Count, 25, 35); // 60s in 2s slices
    }
}

public sealed class SnoozeStoreTests : IDisposable
{
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T10:00:00+00:00");
    readonly string _dir = Directory.CreateTempSubdirectory("keyturn-snooze").FullName;
    DateTimeOffset _now = T0;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    SnoozeStore Store() => new(_dir, () => _now);
    static SecretDefinition Def(string name = "vpn", DateTimeOffset? setAt = null) => new() { Name = name, Schedule = "1h", LastSetAt = setAt };

    [Fact]
    public void SnoozesForTheGivenTimeThenStops()
    {
        var store = Store();
        store.Snooze(Def(), TimeSpan.FromMinutes(10));
        Assert.True(store.IsSnoozed(Def()));
        _now = T0.AddMinutes(9.9);
        Assert.True(store.IsSnoozed(Def()));
        _now = T0.AddMinutes(10);
        Assert.False(store.IsSnoozed(Def()));
    }

    [Fact]
    public void ItIsSharedAcrossInstancesLikeTheWatcherAndAWindow()
    {
        Store().Snooze(Def(), TimeSpan.FromMinutes(10));
        Assert.True(Store().IsSnoozed(Def()));
        Assert.Equal(T0.AddMinutes(10), Store().SnoozedUntil(Def()));
    }

    [Fact]
    public void ARotatedValueVoidsTheSnooze()
    {
        var store = Store();
        store.Snooze(Def(setAt: T0), TimeSpan.FromMinutes(10));
        Assert.True(store.IsSnoozed(Def(setAt: T0)));
        Assert.False(store.IsSnoozed(Def(setAt: T0.AddMinutes(1)))); // set again: a different value, a different snooze
    }

    [Fact]
    public void NamesAreCaseInsensitive()
    {
        Store().Snooze(Def("VPN"), TimeSpan.FromMinutes(10));
        Assert.True(Store().IsSnoozed(Def("vpn")));
    }

    [Fact]
    public void ExpiredEntriesArePrunedOnWrite()
    {
        var store = Store();
        store.Snooze(Def("old"), TimeSpan.FromMinutes(1));
        _now = T0.AddMinutes(5);
        store.Snooze(Def("new"), TimeSpan.FromMinutes(10));
        Assert.DoesNotContain("old", File.ReadAllText(Path.Combine(_dir, SnoozeStore.FileName)));
        Assert.Equal(["new"], Store().Active().Select(a => a.Name));
    }

    [Fact]
    public void ADamagedFileMeansNothingIsSnoozed()
    {
        File.WriteAllText(Path.Combine(_dir, SnoozeStore.FileName), "<<< garbage");
        Assert.False(Store().IsSnoozed(Def()));
        Store().Snooze(Def(), TimeSpan.FromMinutes(10)); // and it can be written over
        Assert.True(Store().IsSnoozed(Def()));
    }

    [Fact]
    public void ADiskThatCannotBeWrittenStillSnoozesInMemory()
    {
        var blocked = Path.Combine(_dir, "no", "such", "place");
        File.WriteAllText(Path.Combine(_dir, "no"), "a file where the directory should be");
        var store = new SnoozeStore(blocked, () => _now);
        store.Snooze(Def(), TimeSpan.FromMinutes(10)); // must not throw
        Assert.True(store.IsSnoozed(Def()));          // and must not turn into a re-prompt loop
    }
}

public sealed class NewSecretGraceTests : IDisposable
{
    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T10:00:00+00:00");
    readonly string _dir = Directory.CreateTempSubdirectory("keyturn-grace").FullName;
    DateTimeOffset _now = T0;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    SnoozeStore Store() => new(_dir, () => _now);
    static SecretDefinition Def(DateTimeOffset? created, DateTimeOffset? setAt = null) =>
        new() { Name = "vpn", Schedule = "1d", CreatedAt = created, LastSetAt = setAt };

    [Fact]
    public void ANewSecretWithoutAValueIsHeldBackForTenMinutes()
    {
        var store = Store();
        Assert.Equal(T0.AddMinutes(10), store.SnoozedUntil(Def(T0)));
        _now = T0.AddMinutes(9.99);
        Assert.True(store.IsSnoozed(Def(T0)));
        _now = T0.AddMinutes(10);
        Assert.False(store.IsSnoozed(Def(T0))); // and from then on it prompts like any other
    }

    [Fact]
    public void ItNeverAppliesOnceTheSecretHasAValue() =>
        Assert.False(Store().IsSnoozed(Def(T0, setAt: T0)));

    [Fact]
    public void SecretsFromEarlierVersionsGetNoGrace() =>
        Assert.False(Store().IsSnoozed(Def(created: null)));

    [Fact]
    public void ASecretLeftWithoutAValueForLongGetsNoGrace()
    {
        _now = T0.AddDays(3);
        Assert.False(Store().IsSnoozed(Def(T0)));
    }

    [Fact]
    public void WhenBothApplyTheLaterEndWins()
    {
        var store = Store();
        _now = T0.AddMinutes(5);
        store.Snooze(Def(T0), TimeSpan.FromMinutes(10)); // dismissed at +5m: until +15m, past the grace end of +10m
        Assert.Equal(T0.AddMinutes(15), store.SnoozedUntil(Def(T0)));
    }
}

public sealed class VaultCreationTests : IDisposable
{
    sealed class NullBackend : ISecretBackend
    {
        public string? Get(string name) => null;
        public void Set(string name, string value) { }
        public void Remove(string name) { }
    }

    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T10:00:00+00:00");
    readonly string _dir = Directory.CreateTempSubdirectory("keyturn-created").FullName;
    DateTimeOffset _now = T0;
    public void Dispose() => Directory.Delete(_dir, recursive: true);
    SecretVault Vault() => new(new NullBackend(), Path.Combine(_dir, "secrets.json"), () => _now);

    [Fact]
    public void DefiningRecordsWhenItWasCreatedAndRedefiningKeepsIt()
    {
        var v = Vault();
        Assert.Equal(T0, v.Define("vpn", "1d").CreatedAt);
        _now = T0.AddDays(1);
        Assert.Equal(T0, v.Define("vpn", "12h", "changed").CreatedAt); // editing the schedule isn't creating it again
    }

    [Fact]
    public void FilesWrittenByEarlierVersionsStillLoad()
    {
        File.WriteAllText(Path.Combine(_dir, "secrets.json"), "[ { \"Name\": \"old\", \"Schedule\": \"1d\" } ]");
        var def = Vault().Find("old")!;
        Assert.Null(def.CreatedAt);
        Assert.Null(def.LastSetAt);
    }
}

public sealed class VaultExpiryTests : IDisposable
{
    sealed class CountingBackend : ISecretBackend
    {
        public int Reads;
        public string? Get(string name) { Reads++; return "x"; }
        public void Set(string name, string value) { }
        public void Remove(string name) { }
    }

    static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-01-01T10:00:00+00:00");
    readonly string _dir = Directory.CreateTempSubdirectory("keyturn-expiry").FullName;
    readonly CountingBackend _backend = new();
    DateTimeOffset _now = T0;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    SecretVault Vault() => new(_backend, Path.Combine(_dir, "secrets.json"), () => _now);

    [Fact]
    public void ExpiredUsesOnlyTheDefinitionsAndNeverTheKeyring()
    {
        var v = Vault();
        v.Define("never", "1h");
        v.Define("old", "1h"); v.Set("old", "x");
        v.Define("fresh", "1d"); v.Set("fresh", "x");
        _now = T0.AddHours(2);
        _backend.Reads = 0;

        var expired = v.Expired();
        Assert.Equal(0, _backend.Reads);
        Assert.Equal(["never", "old"], expired.Select(s => s.Definition.Name).Order());
    }

    [Fact]
    public void NextExpiryIsTheEarliestFreshSecret()
    {
        var v = Vault();
        v.Define("a", "5h"); v.Set("a", "x");
        v.Define("b", "2h"); v.Set("b", "x");
        v.Define("never-set", "1m");
        Assert.Equal(T0.AddHours(2), v.NextExpiry());
    }

    [Fact]
    public void NextExpiryIsNullWhenNothingIsScheduled()
    {
        var v = Vault();
        Assert.Null(v.NextExpiry());
        v.Define("never-set", "1h"); // already due, nothing in the future
        Assert.Null(v.NextExpiry());
    }
}

public sealed class WatchControlTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("keyturn-wc").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void OnlyOneWatcherCanHoldTheLock()
    {
        using var first = WatchLock.TryAcquire(_dir);
        Assert.NotNull(first);
        Assert.True(WatchControl.IsRunning(_dir));
        Assert.Null(WatchLock.TryAcquire(_dir)); // a second watcher exits
    }

    [Fact]
    public void ReleasingTheLockMeansNotRunningAndRemovesThePidFile()
    {
        WatchLock.TryAcquire(_dir)!.Dispose();
        Assert.False(WatchControl.IsRunning(_dir));
        Assert.False(File.Exists(WatchFiles.Pid(_dir)));
        Assert.NotNull(WatchLock.TryAcquire(_dir)); // and a new one can start
    }

    [Fact]
    public void StatusReportsThePidAndStartTimeOfTheRunningWatcher()
    {
        Assert.Null(WatchControl.Info(_dir));
        using var held = WatchLock.TryAcquire(_dir);
        var info = WatchControl.Info(_dir)!;
        Assert.Equal(Environment.ProcessId, info.ProcessId);
        Assert.NotNull(info.StartedAt);
    }

    [Fact]
    public void AStalePidFileIsIgnoredWhenNothingHoldsTheLock()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(WatchFiles.Pid(_dir), "99999\n2026-01-01T00:00:00+00:00\n");
        File.WriteAllText(WatchFiles.Lock(_dir), "");
        Assert.False(WatchControl.IsRunning(_dir));
        Assert.Null(WatchControl.Info(_dir));
    }

    [Fact]
    public void StopRequestsRoundTrip()
    {
        Assert.False(WatchControl.StopRequested(_dir));
        WatchControl.RequestStop(_dir);
        Assert.True(WatchControl.StopRequested(_dir));
        WatchControl.ClearStopRequest(_dir);
        Assert.False(WatchControl.StopRequested(_dir));
    }

    [Fact]
    public void WaitingTimesOutWhileTheWatcherHoldsTheLockAndReturnsOnceItLetsGo()
    {
        var held = WatchLock.TryAcquire(_dir)!;
        Assert.False(WatchControl.WaitUntilStopped(_dir, TimeSpan.FromMilliseconds(300)));
        Task.Run(() => { Thread.Sleep(200); held.Dispose(); });
        Assert.True(WatchControl.WaitUntilStopped(_dir, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void KillRefusesAProcessThatIsNotKeyturn()
    {
        // This test host is "testhost"/"dotnet", not Keyturn: it must refuse, and obviously must not kill itself.
        Assert.False(WatchControl.TryKill(new WatchInfo(Environment.ProcessId, null)));
        Assert.False(WatchControl.TryKill(new WatchInfo(int.MaxValue, null))); // no such process
    }

    [Fact]
    public void TheLogRollsOverInsteadOfGrowingForever()
    {
        var log = new WatchLog(_dir);
        for (var i = 0; i < 6000; i++) log.Write(new string('x', 80) + i);
        Assert.True(new FileInfo(WatchFiles.Log(_dir)).Length < 400 * 1024);
        Assert.EndsWith("5999", log.Tail(1).Single());
    }
}

public class DesktopEnvironmentTests
{
    [Fact]
    public void KeepsOnlyTheDesktopVariables()
    {
        var parsed = DesktopEnvironment.Parse("PATH=/usr/bin\nDISPLAY=:0\nWAYLAND_DISPLAY=wayland-1\nXDG_CURRENT_DESKTOP=Hyprland\nHOME=/home/me\n");
        Assert.Equal(new Dictionary<string, string> { ["DISPLAY"] = ":0", ["WAYLAND_DISPLAY"] = "wayland-1", ["XDG_CURRENT_DESKTOP"] = "Hyprland" }, parsed);
    }

    [Fact]
    public void ToleratesWindowsLineEndingsBlankLinesAndGarbage()
    {
        var parsed = DesktopEnvironment.Parse("\r\nDISPLAY=:1\r\nnot a variable line\r\n=novalue\nWAYLAND_DISPLAY=\n");
        Assert.Equal(new Dictionary<string, string> { ["DISPLAY"] = ":1" }, parsed); // an empty value is no value
    }

    [Fact]
    public void SkipsValuesSystemdQuotesInsteadOfMisreadingThem()
    {
        var parsed = DesktopEnvironment.Parse("XAUTHORITY=$'/home/o\\'brien/.Xauthority'\nDISPLAY=:0\n");
        Assert.Equal(new Dictionary<string, string> { ["DISPLAY"] = ":0" }, parsed);
    }

    [Fact]
    public void ExistingValuesAreNeverOverwrittenOnlyGapsAreFilled()
    {
        // Parse is pure; the fill-in rule is exercised through a fake environment with the same semantics.
        var env = new Dictionary<string, string?> { ["DISPLAY"] = ":9", ["WAYLAND_DISPLAY"] = "" };
        DesktopEnvironment.FillMissingFromSystemd(env); // on a machine without the variables this changes nothing
        Assert.Equal(":9", env["DISPLAY"]);
    }
}
