using System.Runtime.InteropServices;

namespace Keyturn;

/// <summary>
/// Linux Secret Service backend using libsecret directly.
/// GCM's own Linux store writes values with the invalid content type "plain/text", which strict
/// Secret Service implementations (e.g. oo7-daemon) reject; libsecret's simple API uses "text/plain".
/// </summary>
public sealed unsafe class LibsecretBackend : ISecretBackend
{
    const string Lib = "libsecret-1.so.0";
    const string Glib = "libglib-2.0.so.0";
    const string SchemaName = "org.keyturn.Secret";
    const string NameAttribute = "name";

    // SecretSchema is { char* name; int flags; {char* name; int type;} attributes[32]; reserved... }.
    // Built by hand in unmanaged memory to avoid marshalling a fixed-size nested array (64-bit layout).
    static readonly IntPtr Schema = BuildSchema();

    public LibsecretBackend()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        if (!Environment.Is64BitProcess) throw new PlatformNotSupportedException("libsecret backend requires a 64-bit process.");
    }

    public string? Get(string name)
    {
        var attrs = Attributes(name);
        try
        {
            var p = secret_password_lookupv_sync(Schema, attrs.Table, 0, out var err);
            ThrowIfError(err, "Failed to read from the Secret Service");
            if (p == 0) return null;
            try { return Marshal.PtrToStringUTF8(p); }
            finally { secret_password_free(p); }
        }
        finally { attrs.Dispose(); }
    }

    public void Set(string name, string value)
    {
        var attrs = Attributes(name);
        try
        {
            secret_password_storev_sync(Schema, attrs.Table, "default", $"Keyturn: {name}", value, 0, out var err);
            ThrowIfError(err, "Failed to store in the Secret Service");
        }
        finally { attrs.Dispose(); }
    }

    public void Remove(string name)
    {
        var attrs = Attributes(name);
        try
        {
            secret_password_clearv_sync(Schema, attrs.Table, 0, out var err);
            ThrowIfError(err, "Failed to remove from the Secret Service");
        }
        finally { attrs.Dispose(); }
    }

    static IntPtr BuildSchema()
    {
        const int size = 1024;
        var schema = Marshal.AllocHGlobal(size);
        new Span<byte>((void*)schema, size).Clear();
        Marshal.WriteIntPtr(schema, 0, Marshal.StringToCoTaskMemUTF8(SchemaName)); // name
        Marshal.WriteInt32(schema, 8, 0);                                         // flags: NONE
        Marshal.WriteIntPtr(schema, 16, Marshal.StringToCoTaskMemUTF8(NameAttribute)); // attributes[0].name
        Marshal.WriteInt32(schema, 24, 0);                                        // attributes[0].type: STRING
        return schema; // attributes[1].name stays NULL, terminating the list
    }

    static HashTable Attributes(string name) => new(NameAttribute, name);

    static void ThrowIfError(IntPtr error, string message)
    {
        if (error == 0) return;
        var detail = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)); // GError.message
        g_error_free(error);
        throw new InvalidOperationException($"{message}: {detail}");
    }

    /// <summary>A one-entry GHashTable&lt;string,string&gt; whose strings are freed on dispose.</summary>
    readonly struct HashTable : IDisposable
    {
        readonly IntPtr _key, _value;
        public IntPtr Table { get; }

        public HashTable(string key, string value)
        {
            _key = Marshal.StringToCoTaskMemUTF8(key);
            _value = Marshal.StringToCoTaskMemUTF8(value);
            var glib = NativeLibrary.Load(Glib);
            Table = g_hash_table_new(NativeLibrary.GetExport(glib, "g_str_hash"), NativeLibrary.GetExport(glib, "g_str_equal"));
            g_hash_table_insert(Table, _key, _value);
        }

        public void Dispose()
        {
            g_hash_table_unref(Table);
            Marshal.FreeCoTaskMem(_key);
            Marshal.FreeCoTaskMem(_value);
        }
    }

    [DllImport(Glib)] static extern IntPtr g_hash_table_new(IntPtr hash, IntPtr equal);
    [DllImport(Glib)] static extern int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
    [DllImport(Glib)] static extern void g_hash_table_unref(IntPtr table);
    [DllImport(Glib)] static extern void g_error_free(IntPtr error);

    [DllImport(Lib)]
    static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string collection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string label,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string password,
        IntPtr cancellable, out IntPtr error);

    [DllImport(Lib)] static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [DllImport(Lib)] static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [DllImport(Lib)] static extern void secret_password_free(IntPtr password);
}
