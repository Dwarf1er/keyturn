using System.Xml.Linq;
using Keyturn.Cli;

namespace Keyturn.Tests;

/// <summary>macOS packaging logic that can be verified on any OS: where the app is found, and the launchd job.</summary>
public class AppLocatorTests
{
    // Paths are built with Path.Combine, exactly as the code under test builds them, so these tests check the lookup
    // logic and not the host OS's path syntax (on Windows Path.Combine inserts backslashes).
    static string P(params string[] parts) => Path.Combine(parts);

    static readonly string Base = P("/opt", "keyturn", "bin");
    const string Bundle = "Keyturn.app/Contents/MacOS/Keyturn.App";

    static string BundleIn(string folder) => P(folder, Bundle);

    static string? FindOnMac(IEnumerable<string> existingFiles, string? explicitPath = null, IEnumerable<string>? existingDirs = null)
    {
        var files = existingFiles.ToHashSet();
        var dirs = (existingDirs ?? []).ToHashSet();
        return AppLocator.Find(Base, isWindows: false, isMacOS: true, explicitPath, homeDirectory: "/Users/me", files.Contains, dirs.Contains);
    }

    [Fact]
    public void MacPrefersTheBundleBesideTheCliOverABareBinary() =>
        Assert.Equal(BundleIn(Base), FindOnMac([P(Base, "Keyturn.App"), BundleIn(Base)]));

    [Fact]
    public void MacFallsBackToAnUnbundledBinaryBesideTheCli() =>
        Assert.Equal(P(Base, "Keyturn.App"), FindOnMac([P(Base, "Keyturn.App")]));

    [Fact]
    public void MacFindsTheBundleInUserApplications() =>
        Assert.Equal(BundleIn(P("/Users/me", "Applications")), FindOnMac([BundleIn(P("/Users/me", "Applications"))]));

    [Fact]
    public void MacFindsTheBundleInSystemApplicationsLast() =>
        Assert.Equal(BundleIn("/Applications"), FindOnMac([BundleIn("/Applications")]));

    [Fact]
    public void MacPrefersUserApplicationsOverSystemApplications() =>
        Assert.Equal(BundleIn(P("/Users/me", "Applications")), FindOnMac([BundleIn("/Applications"), BundleIn(P("/Users/me", "Applications"))]));

    [Fact]
    public void NothingFoundIsNull() => Assert.Null(FindOnMac([]));

    [Fact]
    public void ExplicitBundleFolderIsResolvedToItsExecutable()
    {
        var folder = P("/x", "Keyturn.app");
        var executable = P(folder, "Contents", "MacOS", "Keyturn.App");
        Assert.Equal(executable, FindOnMac([executable], explicitPath: folder, existingDirs: [folder]));
    }

    [Fact]
    public void ExplicitPathNeverFallsBackToAnotherInstall() =>
        Assert.Null(FindOnMac([BundleIn(Base)], explicitPath: P("/nope", "Keyturn.App")));

    [Fact]
    public void LinuxLooksOnlyBesideTheCli()
    {
        Assert.Equal(P(Base, "Keyturn.App"), AppLocator.Find(Base, false, false, fileExists: p => p == P(Base, "Keyturn.App")));
        Assert.Null(AppLocator.Find(Base, false, false, homeDirectory: "/home/me", fileExists: p => p.Contains("Applications")));
    }

    [Fact]
    public void WindowsLooksForTheExe() =>
        Assert.Equal(P(Base, "Keyturn.App.exe"), AppLocator.Find(Base, true, false, fileExists: p => p.EndsWith("Keyturn.App.exe")));
}
