// Adapted from Git Credential Manager (MIT License, Copyright GitHub, Inc. and contributors).
// https://github.com/git-ecosystem/git-credential-manager @ 91d6da2f699d60802cdd5d339ba2b91b38dce549
// See ThirdParty/GitCredentialManager/README.md for what was changed.
#nullable disable
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FILETIME = System.Runtime.InteropServices.ComTypes.FILETIME;

namespace Keyturn.Interop.Windows;

// https://docs.microsoft.com/en-us/windows/desktop/api/wincred/
internal static class Advapi32
{
    const string LibraryName = "advapi32.dll";

    [DllImport(LibraryName, EntryPoint = "CredReadW", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredRead(
        string target,
        CredentialType type,
        int reserved,
        out IntPtr credential);

    [DllImport(LibraryName, EntryPoint = "CredWriteW", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredWrite(
        ref Win32Credential credential,
        int flags);

    [DllImport(LibraryName, EntryPoint = "CredDeleteW", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredDelete(
        string target,
        CredentialType type,
        int flags);

    [DllImport(LibraryName, EntryPoint = "CredFree", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern void CredFree(
        IntPtr credential);
}

// Enum values from wincred.h
internal enum CredentialType
{
    Generic = 1,
}

internal enum CredentialPersist
{
    None = 0,
    Session = 1,
    LocalMachine = 2,
    Enterprise = 3,
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct Win32Credential
{
    public int Flags;
    public CredentialType Type;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string TargetName;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string Comment;
    public FILETIME LastWritten;
    public int CredentialBlobSize;
    public IntPtr CredentialBlob;
    public CredentialPersist Persist;
    public int AttributeCount;
    public IntPtr Attributes;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string TargetAlias;
    [MarshalAs(UnmanagedType.LPWStr)]
    public string UserName;

    public string GetCredentialBlobAsString()
    {
        if (CredentialBlobSize != 0 && CredentialBlob != IntPtr.Zero)
        {
            var passwordBytes = new byte[CredentialBlobSize];
            Marshal.Copy(CredentialBlob, passwordBytes, 0, passwordBytes.Length);
            return Encoding.Unicode.GetString(passwordBytes);
        }

        return null;
    }
}

internal static class Win32Error
{
    public const int Success = 0;

    /// <summary>Element not found.</summary>
    public const int NotFound = 1168;

    public static int GetLastError(bool success) => success ? Success : Marshal.GetLastWin32Error();

    public static void ThrowIfError(int error, string defaultErrorMessage = "Unknown error.")
    {
        if (error == Success) return;
        // The Win32Exception constructor automatically gets the human-readable message for the error code.
        throw new InteropException(defaultErrorMessage, new Win32Exception(error));
    }
}
