// Adapted from Git Credential Manager (MIT License, Copyright GitHub, Inc. and contributors).
// https://github.com/git-ecosystem/git-credential-manager @ 91d6da2f699d60802cdd5d339ba2b91b38dce549
// See ThirdParty/GitCredentialManager/README.md for what was changed.
#nullable disable
using System;
using System.ComponentModel;

namespace Keyturn.Interop;

/// <summary>An unexpected error occurred in interop code.</summary>
public class InteropException : Exception
{
    public InteropException(string message, int errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public InteropException(string message, Win32Exception w32Exception)
        : base(message, w32Exception)
    {
        ErrorCode = w32Exception.NativeErrorCode;
    }

    /// <summary>Native error code.</summary>
    public int ErrorCode { get; }
}
