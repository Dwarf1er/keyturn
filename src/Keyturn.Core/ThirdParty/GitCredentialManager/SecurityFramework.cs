// Adapted from Git Credential Manager (MIT License, Copyright GitHub, Inc. and contributors).
// https://github.com/git-ecosystem/git-credential-manager @ 91d6da2f699d60802cdd5d339ba2b91b38dce549
// See ThirdParty/GitCredentialManager/README.md for what was changed.
#nullable disable
using System;
using System.Runtime.InteropServices;

namespace Keyturn.Interop.MacOS;

// https://developer.apple.com/documentation/security/keychain_services/keychain_items
internal static class SecurityFramework
{
    const string SecurityFrameworkLib = "/System/Library/Frameworks/Security.framework/Security";

    [DllImport(SecurityFrameworkLib, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SecKeychainAddGenericPassword(
        IntPtr keychain,
        uint serviceNameLength,
        string serviceName,
        uint accountNameLength,
        string accountName,
        uint passwordLength,
        byte[] passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFrameworkLib, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SecKeychainFindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceNameLength,
        string serviceName,
        uint accountNameLength,
        string accountName,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr itemRef);

    [DllImport(SecurityFrameworkLib, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SecKeychainItemModifyAttributesAndData(
        IntPtr itemRef,
        IntPtr attrList, // SecKeychainAttributeList*
        uint length,
        byte[] data);

    [DllImport(SecurityFrameworkLib, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SecKeychainItemDelete(
        IntPtr itemRef);

    [DllImport(SecurityFrameworkLib, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    public static extern int SecKeychainItemFreeContent(
        IntPtr attrList, // SecKeychainAttributeList*
        IntPtr data);

    // https://developer.apple.com/documentation/security/1542001-security_framework_result_codes
    public const int OK = 0;
    public const int ErrorSecNoSuchKeychain = -25294;
    public const int ErrorSecInvalidKeychain = -25295;
    public const int ErrorSecAuthFailed = -25293;
    public const int ErrorSecDuplicateItem = -25299;
    public const int ErrorSecItemNotFound = -25300;
    public const int ErrorSecInteractionNotAllowed = -25308;
    public const int ErrorSecInteractionRequired = -25315;
    public const int ErrorSecNoSuchAttr = -25303;

    public static void ThrowIfError(int error, string defaultErrorMessage = "Unknown error.")
    {
        switch (error)
        {
            case OK:
                return;
            case ErrorSecNoSuchKeychain:
                throw new InteropException("The keychain does not exist.", error);
            case ErrorSecInvalidKeychain:
                throw new InteropException("The keychain is not valid.", error);
            case ErrorSecAuthFailed:
                throw new InteropException("Authorization/Authentication failed.", error);
            case ErrorSecDuplicateItem:
                throw new InteropException("The item already exists.", error);
            case ErrorSecItemNotFound:
                throw new InteropException("The item cannot be found.", error);
            case ErrorSecInteractionNotAllowed:
                throw new InteropException("Interaction with the Security Server is not allowed.", error);
            case ErrorSecInteractionRequired:
                throw new InteropException("User interaction is required.", error);
            case ErrorSecNoSuchAttr:
                throw new InteropException("The attribute does not exist.", error);
            default:
                throw new InteropException(defaultErrorMessage, error);
        }
    }
}

internal static class CoreFoundation
{
    const string CoreFoundationFrameworkLib = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(CoreFoundationFrameworkLib, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.Cdecl)]
    public static extern void CFRelease(IntPtr cf);
}
