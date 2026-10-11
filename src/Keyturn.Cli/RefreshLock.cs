namespace Keyturn.Cli;

/// <summary>
/// Cross-process lock so that many tools finding the same secret expired produce one prompt, not many.
/// Held for the life of the object; released on dispose (or when the process dies).
/// </summary>
sealed class RefreshLock : IDisposable
{
    readonly FileStream _stream;
    RefreshLock(FileStream stream) => _stream = stream;

    public static RefreshLock? TryAcquire(string path, TimeSpan wait)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var deadline = DateTime.UtcNow + wait;
        while (true)
        {
            try
            {
                return new RefreshLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException)
            {
                if (DateTime.UtcNow >= deadline) return null;
                Thread.Sleep(150);
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
