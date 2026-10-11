using System.Runtime.InteropServices;
using System.Text;
using Keyturn.Interop.Windows;

namespace Keyturn;

/// <summary>
/// Windows Credential Manager, as generic credentials named "keyturn:NAME" (user name NAME),
/// visible in Control Panel > Credential Manager > Windows Credentials.
/// </summary>
public sealed class WindowsCredentialBackend : ISecretBackend
{
    const string Prefix = "keyturn:";

    /// <summary>Credential Manager stores at most 2,560 bytes per secret, i.e. 1,280 UTF-16 characters.</summary>
    internal const int MaxChars = 1_280;

    public WindowsCredentialBackend()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
    }

    public string? Get(string name)
    {
        IntPtr ptr = IntPtr.Zero;
        try
        {
            if (!Advapi32.CredRead(Prefix + name, CredentialType.Generic, 0, out ptr))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == Win32Error.NotFound) return null;
                Win32Error.ThrowIfError(error, "Failed to read item from store.");
            }
            return Marshal.PtrToStructure<Win32Credential>(ptr).GetCredentialBlobAsString();
        }
        finally { if (ptr != IntPtr.Zero) Advapi32.CredFree(ptr); }
    }

    public void Set(string name, string value)
    {
        EnsureFits(value);
        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new Win32Credential
            {
                Type = CredentialType.Generic,
                TargetName = Prefix + name,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersist.LocalMachine,
                UserName = name,
            };
            Win32Error.ThrowIfError(Win32Error.GetLastError(Advapi32.CredWrite(ref credential, 0)), "Failed to write item to store.");
        }
        finally { Marshal.FreeHGlobal(blob); }
    }

    public void Remove(string name)
    {
        var error = Win32Error.GetLastError(Advapi32.CredDelete(Prefix + name, CredentialType.Generic, 0));
        if (error != Win32Error.NotFound) Win32Error.ThrowIfError(error, "Failed to remove item from store.");
    }

    internal static void EnsureFits(string value)
    {
        if (value.Length > MaxChars)
            throw new ArgumentException($"Windows Credential Manager can store at most {MaxChars} characters per secret (this one is {value.Length}).", nameof(value));
    }
}
