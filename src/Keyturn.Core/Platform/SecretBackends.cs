namespace Keyturn;

/// <summary>Picks the credential store for this OS.</summary>
public static class SecretBackends
{
    public static ISecretBackend CreateDefault() =>
        OperatingSystem.IsWindows() ? new WindowsCredentialBackend()
        : OperatingSystem.IsMacOS() ? new MacOsKeychainBackend()
        : new LibsecretBackend();
}
