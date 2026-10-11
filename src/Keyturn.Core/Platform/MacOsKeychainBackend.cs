using System.Runtime.InteropServices;
using System.Text;
using Keyturn.Interop.MacOS;
using static Keyturn.Interop.MacOS.CoreFoundation;
using static Keyturn.Interop.MacOS.SecurityFramework;

namespace Keyturn;

/// <summary>
/// macOS login keychain via the same <c>SecKeychain*</c> calls Git Credential Manager uses.
/// Items are generic passwords with service "keyturn" and the secret's name as account, so scripts can read
/// them with: <c>security find-generic-password -s keyturn -a NAME -w</c>.
/// </summary>
public sealed class MacOsKeychainBackend : ISecretBackend
{
    const string Service = "keyturn";

    public MacOsKeychainBackend()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
    }

    public string? Get(string name)
    {
        IntPtr data = IntPtr.Zero, item = IntPtr.Zero;
        try
        {
            var rc = Find(name, out var length, out data, out item);
            if (rc == ErrorSecItemNotFound) return null;
            ThrowIfError(rc, "Could not read item");

            var bytes = new byte[length];
            Marshal.Copy(data, bytes, 0, bytes.Length);
            return Encoding.UTF8.GetString(bytes);
        }
        finally { Free(data, item); }
    }

    public void Set(string name, string value)
    {
        var secret = Encoding.UTF8.GetBytes(value);
        IntPtr data = IntPtr.Zero, item = IntPtr.Zero, created = IntPtr.Zero;
        try
        {
            var rc = Find(name, out _, out data, out item);
            switch (rc)
            {
                case OK:
                    ThrowIfError(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)secret.Length, secret), "Could not update existing item");
                    break;
                case ErrorSecItemNotFound:
                    ThrowIfError(
                        SecKeychainAddGenericPassword(IntPtr.Zero, ByteCount(Service), Service, ByteCount(name), name, (uint)secret.Length, secret, out created),
                        "Could not create new item");
                    break;
                default:
                    ThrowIfError(rc);
                    break;
            }
        }
        finally
        {
            Free(data, item);
            if (created != IntPtr.Zero) CFRelease(created);
        }
    }

    public void Remove(string name)
    {
        IntPtr data = IntPtr.Zero, item = IntPtr.Zero;
        try
        {
            var rc = Find(name, out _, out data, out item);
            if (rc == ErrorSecItemNotFound) return;
            ThrowIfError(rc);
            ThrowIfError(SecKeychainItemDelete(item));
        }
        finally { Free(data, item); }
    }

    static int Find(string name, out uint length, out IntPtr data, out IntPtr item) =>
        SecKeychainFindGenericPassword(IntPtr.Zero, ByteCount(Service), Service, ByteCount(name), name, out length, out data, out item);

    // Keychain lengths are in bytes of the UTF-8 string the marshaller sends, not UTF-16 characters.
    static uint ByteCount(string s) => (uint)Encoding.UTF8.GetByteCount(s);

    static void Free(IntPtr data, IntPtr item)
    {
        if (data != IntPtr.Zero) SecKeychainItemFreeContent(IntPtr.Zero, data);
        if (item != IntPtr.Zero) CFRelease(item);
    }
}
