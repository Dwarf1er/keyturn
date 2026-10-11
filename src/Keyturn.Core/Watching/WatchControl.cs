using System.Diagnostics;

namespace Keyturn;

/// <summary>The files the background watcher keeps in the config directory.</summary>
public static class WatchFiles
{
    public static string Lock(string configDirectory) => Path.Combine(configDirectory, "watch.lock");
    public static string Pid(string configDirectory) => Path.Combine(configDirectory, "watch.pid");
    public static string Stop(string configDirectory) => Path.Combine(configDirectory, "watch.stop");
    public static string Log(string configDirectory) => Path.Combine(configDirectory, "watch.log");
}

/// <summary>
/// Held by the one running watcher per user for its whole life: a second watcher fails to acquire it and exits.
/// The OS releases it if the process dies, so a crash never leaves a stale "running" state behind.
/// </summary>
public sealed class WatchLock : IDisposable
{
    readonly FileStream _stream;
    readonly string _configDirectory;

    WatchLock(FileStream stream, string configDirectory)
    {
        _stream = stream;
        _configDirectory = configDirectory;
    }

    public static WatchLock? TryAcquire(string configDirectory)
    {
        Directory.CreateDirectory(configDirectory);
        FileStream stream;
        try { stream = new FileStream(WatchFiles.Lock(configDirectory), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }

        // Informational only (the lock is the source of truth): lets `keyturn watch status` show the pid and start time.
        File.WriteAllText(WatchFiles.Pid(configDirectory), $"{Environment.ProcessId}\n{DateTimeOffset.Now:O}\n");
        return new WatchLock(stream, configDirectory);
    }

    public void Dispose()
    {
        _stream.Dispose();
        try { File.Delete(WatchFiles.Pid(_configDirectory)); } catch (IOException) { }
    }
}

public sealed record WatchInfo(int ProcessId, DateTimeOffset? StartedAt);

/// <summary>Asking the watcher about itself, and asking it to stop. Works through the files above, so it is the same on every OS.</summary>
public static class WatchControl
{
    public static bool IsRunning(string configDirectory)
    {
        var path = WatchFiles.Lock(configDirectory);
        if (!File.Exists(path)) return false;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false; // we could take the lock, so nobody holds it
        }
        catch (IOException)
        {
            return true;
        }
    }

    /// <summary>The pid and start time the running watcher wrote, or null when none is running.</summary>
    public static WatchInfo? Info(string configDirectory)
    {
        if (!IsRunning(configDirectory)) return null;
        try
        {
            var lines = File.ReadAllLines(WatchFiles.Pid(configDirectory));
            if (lines.Length == 0 || !int.TryParse(lines[0], out var pid)) return null;
            DateTimeOffset? started = lines.Length > 1 && DateTimeOffset.TryParse(lines[1], out var t) ? t : null;
            return new WatchInfo(pid, started);
        }
        catch (IOException) { return null; }
    }

    public static void RequestStop(string configDirectory)
    {
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(WatchFiles.Stop(configDirectory), "stop\n");
    }

    public static bool StopRequested(string configDirectory) => File.Exists(WatchFiles.Stop(configDirectory));

    public static void ClearStopRequest(string configDirectory)
    {
        try { File.Delete(WatchFiles.Stop(configDirectory)); } catch (IOException) { }
    }

    /// <summary>Waits for the watcher to release its lock; true if it did within the timeout.</summary>
    public static bool WaitUntilStopped(string configDirectory, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (IsRunning(configDirectory))
        {
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(100);
        }
        return true;
    }

    /// <summary>Last resort when a stop request isn't honoured. Refuses to kill anything that isn't a Keyturn process.</summary>
    public static bool TryKill(WatchInfo info)
    {
        try
        {
            using var process = Process.GetProcessById(info.ProcessId);
            if (!process.ProcessName.StartsWith("Keyturn", StringComparison.OrdinalIgnoreCase)) return false;
            process.Kill();
            return process.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>A small rolling log, because a background process on Windows has no console to print to.</summary>
public sealed class WatchLog(string configDirectory, Func<DateTimeOffset>? now = null)
{
    const int MaxBytes = 256 * 1024;
    readonly string _path = WatchFiles.Log(configDirectory);
    readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);

    public void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(configDirectory);
            if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
            {
                var keep = File.ReadAllLines(_path);
                File.WriteAllLines(_path, keep.Skip(keep.Length / 2));
            }
            File.AppendAllText(_path, $"{_now():yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the watcher down.
        }
    }

    /// <summary>The last few lines, for `keyturn watch status`.</summary>
    public IReadOnlyList<string> Tail(int lines = 5)
    {
        try { return File.Exists(_path) ? File.ReadAllLines(_path).TakeLast(lines).ToList() : []; }
        catch (IOException) { return []; }
    }
}
