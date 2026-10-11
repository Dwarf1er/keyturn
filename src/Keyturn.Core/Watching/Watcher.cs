namespace Keyturn;

/// <summary>Shows the prompt window and returns when it closes. The exit code is 0 for a normal close, anything else for a failure.</summary>
public interface IPromptRunner
{
    Task<int> RunAsync(CancellationToken cancellationToken);
}

public sealed record WatcherOptions
{
    /// <summary>How long a dismissed ("Later") secret is left alone.</summary>
    public TimeSpan Snooze { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How long to wait before trying again when the prompt failed to open (no display yet, a crash).</summary>
    public TimeSpan RetryAfterFailure { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest it sleeps at a time, which is how quickly it notices new secrets, changes and stop requests.</summary>
    public TimeSpan MaxSlice { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often it also checks the credential store itself (a value deleted behind its back), not only the schedules.</summary>
    public TimeSpan FullCheckEvery { get; init; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// The background loop: sleep until the earliest secret expires, then show the prompt. It sleeps in short slices instead
/// of one long wait, so a sleep/resume, a clock change, an edited secrets file or a stop request is noticed within a
/// few seconds. Time and sleeping are injected so the whole thing can be tested with a fake clock.
/// </summary>
public sealed class Watcher(
    SecretVault vault,
    SnoozeStore snooze,
    IPromptRunner prompts,
    WatcherOptions? options = null,
    Func<DateTimeOffset>? now = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<bool>? stopRequested = null,
    Action<string>? log = null)
{
    readonly WatcherOptions _options = options ?? new WatcherOptions();
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    readonly Func<bool> _stopRequested = stopRequested ?? (() => false);
    readonly Action<string> _log = log ?? (_ => { });

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _log("watcher started");
        var nextFullCheck = DateTimeOffset.MinValue; // the first pass also checks the credential store
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_stopRequested())
            {
                var started = _now();
                try
                {
                    IReadOnlyList<SecretStatus> expired;
                    if (started >= nextFullCheck)
                    {
                        nextFullCheck = started + _options.FullCheckEvery;
                        try { expired = vault.Due(); }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // E.g. the keyring isn't unlocked yet at login. Schedules alone are still enough to prompt.
                            _log($"credential store unavailable, checking schedules only: {ex.Message}");
                            expired = vault.Expired();
                        }
                    }
                    else
                    {
                        expired = vault.Expired();
                    }

                    if (await PromptIfNeededAsync(expired, cancellationToken)) continue;
                    await _delay(TimeUntilNextEvent(started, expired), cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A damaged secrets file or a transient IO error must not kill a process that is meant to run for weeks.
                    _log($"error: {ex.Message}");
                    await _delay(_options.MaxSlice, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // asked to stop
        }
        _log("watcher stopped");
    }

    /// <summary>Opens the prompt when something is expired, undismissed and not already being asked for. Returns whether it did.</summary>
    async Task<bool> PromptIfNeededAsync(IReadOnlyList<SecretStatus> expired, CancellationToken cancellationToken)
    {
        var locks = new List<RefreshLock>();
        var busy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var status in expired)
            {
                if (snooze.IsSnoozed(status.Definition)) continue;
                // A tool's `keyturn get --refresh` may already have a window open for this secret: don't open a second.
                var gate = RefreshLock.TryAcquire(vault.RefreshLockPath(status.Definition.Name), TimeSpan.Zero);
                if (gate is null) busy.Add(status.Definition.Name);
                else locks.Add(gate);
            }
            if (locks.Count == 0) return false;

            _log($"{locks.Count} secret(s) expired, opening the prompt");
            int exitCode;
            try { exitCode = await prompts.RunAsync(cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log($"could not open the prompt: {ex.Message}");
                exitCode = -1;
            }

            // Whatever is still expired now was dismissed (or the window failed): don't ask again straight away.
            var wait = exitCode == 0 ? _options.Snooze : _options.RetryAfterFailure;
            foreach (var status in vault.Expired())
            {
                if (busy.Contains(status.Definition.Name) || snooze.IsSnoozed(status.Definition)) continue;
                snooze.Snooze(status.Definition, wait);
            }
            _log(exitCode == 0 ? "prompt closed" : $"the prompt exited with code {exitCode}; trying again in {wait.TotalSeconds:0}s");
            return true;
        }
        finally
        {
            foreach (var gate in locks) gate.Dispose();
        }
    }

    TimeSpan TimeUntilNextEvent(DateTimeOffset now, IReadOnlyList<SecretStatus> expired)
    {
        var wake = now + _options.MaxSlice;
        if (vault.NextExpiry() is { } nextExpiry && nextExpiry < wake) wake = nextExpiry;
        foreach (var status in expired)
            if (snooze.SnoozedUntil(status.Definition) is { } until && until < wake) wake = until;

        var wait = wake - now;
        return wait < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : wait; // never a busy loop
    }
}
