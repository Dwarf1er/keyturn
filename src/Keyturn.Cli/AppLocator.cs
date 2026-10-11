namespace Keyturn.Cli;

/// <summary>
/// Finds the Keyturn app. It lives beside the CLI in release archives and on Linux/Windows installs; on macOS it is
/// <c>Keyturn.app</c>, a bundle (the Dock and Finder only take icons from bundles), which may sit beside the CLI or in
/// ~/Applications, where the installer puts it. Pure so every layout can be tested on any OS.
/// </summary>
static class AppLocator
{
    /// <summary>The executable inside a macOS bundle. Launched directly, not through `open`, so exit codes and waiting work.</summary>
    public const string BundleExecutable = "Keyturn.app/Contents/MacOS/Keyturn.App";

    public static string? Find(
        string baseDirectory, bool isWindows, bool isMacOS, string? explicitPath = null, string? homeDirectory = null,
        Func<string, bool>? fileExists = null, Func<string, bool>? directoryExists = null)
    {
        fileExists ??= File.Exists;
        directoryExists ??= Directory.Exists;

        if (!string.IsNullOrEmpty(explicitPath))
        {
            var resolved = ResolveBundle(explicitPath, directoryExists);
            return fileExists(resolved) ? resolved : null; // an explicit choice never silently falls back
        }

        var candidates = new List<string>();
        if (isWindows)
        {
            candidates.Add(Path.Combine(baseDirectory, "Keyturn.App.exe"));
        }
        else if (isMacOS)
        {
            candidates.Add(Path.Combine(baseDirectory, BundleExecutable));
            candidates.Add(Path.Combine(baseDirectory, "Keyturn.App")); // an unbundled build, e.g. from `dotnet publish`
            if (!string.IsNullOrEmpty(homeDirectory)) candidates.Add(Path.Combine(homeDirectory, "Applications", BundleExecutable));
            candidates.Add(Path.Combine("/Applications", BundleExecutable));
        }
        else
        {
            candidates.Add(Path.Combine(baseDirectory, "Keyturn.App"));
        }

        return candidates.FirstOrDefault(fileExists);
    }

    public static string? Find(string? explicitPath = null) => Find(
        AppContext.BaseDirectory, OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(),
        explicitPath ?? Environment.GetEnvironmentVariable("KEYTURN_APP"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Accepts the bundle folder itself (…/Keyturn.app) wherever an app path is expected.</summary>
    public static string ResolveBundle(string path, Func<string, bool> directoryExists) =>
        path.TrimEnd('/', '\\').EndsWith(".app", StringComparison.OrdinalIgnoreCase) && directoryExists(path)
            ? Path.Combine(path, "Contents", "MacOS", "Keyturn.App")
            : path;
}
