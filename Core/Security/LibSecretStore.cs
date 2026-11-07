using System;
using System.Buffers.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace erp_inteligente.Core.Security;

/// <summary>
/// Linux key store backed by the freedesktop Secret Service through libsecret (GNOME Keyring, KWallet/ksecretd, ...).
/// Items use the schema <c>app.nexus.erp</c> with string attributes <c>db</c> and <c>key</c>; the 32-byte secret is
/// stored as base64 text because libsecret passwords are NUL-terminated strings.
/// Only the non-variadic <c>*v_sync</c> entry points are used, so no C varargs are needed.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LibSecretStore : ISecretStore
{
    private const string SecretLib = "libsecret-1.so.0";
    private const string GlibLib = "libglib-2.0.so.0";
    private const string SchemaName = "app.nexus.erp";

    // struct SecretSchema (libsecret-1, 64-bit): const gchar *name @0; SecretSchemaFlags flags @8;
    // SecretSchemaAttribute attributes[32] @16 (each: const gchar *name; SecretSchemaAttributeType type; = 16 bytes);
    // gint reserved; gpointer reserved1..7 -> total 16 + 512 + 8 + 56 = 592 bytes.
    private const int SchemaSize = 592;
    private const int SchemaAttributesOffset = 16;
    private const int SchemaAttributeStride = 16;

    private static readonly Lazy<IntPtr> Schema = new(BuildSchema);

    public byte[]? Get(string databaseId, string keyName)
    {
        using var attrs = new AttributeTable(databaseId, keyName);
        var result = Call(() =>
        {
            var p = secret_password_lookupv_sync(Schema.Value, attrs.Handle, IntPtr.Zero, out var err);
            return (p, err);
        }, "read");
        if (result == IntPtr.Zero) return null;
        try
        {
            return DecodeSecret(result);
        }
        finally
        {
            secret_password_free(result); // wipes the string before freeing
        }
    }

    public void Set(string databaseId, string keyName, byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        using var attrs = new AttributeTable(databaseId, keyName);
        var label = Marshal.StringToCoTaskMemUTF8($"Nexus {keyName} key for {databaseId}");
        var collection = Marshal.StringToCoTaskMemUTF8("default");
        var (native, nativeLen) = EncodeSecret(secret);
        try
        {
            var ok = false;
            Call(() =>
            {
                ok = secret_password_storev_sync(Schema.Value, attrs.Handle, collection, label, native,
                    IntPtr.Zero, out var err) != 0;
                return (IntPtr.Zero, err);
            }, "write");
            if (!ok) throw new SecretStoreUnavailableException("The Secret Service refused to store the key.");
        }
        finally
        {
            ZeroNative(native, nativeLen);
            Marshal.FreeHGlobal(native);
            Marshal.FreeCoTaskMem(label);
            Marshal.FreeCoTaskMem(collection);
        }
    }

    public void Delete(string databaseId, string keyName)
    {
        using var attrs = new AttributeTable(databaseId, keyName);
        Call(() =>
        {
            _ = secret_password_clearv_sync(Schema.Value, attrs.Handle, IntPtr.Zero, out var err);
            return (IntPtr.Zero, err);
        }, "delete");
    }

    /// <summary>Runs a native call, converting interop failures and GError results into <see cref="SecretStoreUnavailableException"/>.</summary>
    private static IntPtr Call(Func<(IntPtr Result, IntPtr Error)> action, string operation)
    {
        try
        {
            var (result, error) = action();
            if (error != IntPtr.Zero)
            {
                if (result != IntPtr.Zero) secret_password_free(result);
                throw new SecretStoreUnavailableException(
                    $"The Secret Service failed to {operation} the key: {ConsumeError(error)}");
            }
            return result;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            throw new SecretStoreUnavailableException("libsecret is not available on this system.", e);
        }
    }

    private static string ConsumeError(IntPtr error)
    {
        try
        {
            // struct GError { GQuark domain (guint32); gint code; gchar *message; }
            var msg = Marshal.ReadIntPtr(error, 8);
            return (msg == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(msg)) ?? "unknown error";
        }
        finally
        {
            g_error_free(error);
        }
    }

    private static byte[] DecodeSecret(IntPtr text)
    {
        var len = 0;
        while (Marshal.ReadByte(text, len) != 0)
        {
            if (++len > 4096) throw new SecretStoreUnavailableException("The stored key has an unexpected format.");
        }
        var utf8 = new byte[len];
        var decoded = new byte[len];
        try
        {
            Marshal.Copy(text, utf8, 0, len);
            if (Base64.DecodeFromUtf8(utf8, decoded, out _, out var written) != System.Buffers.OperationStatus.Done)
                throw new SecretStoreUnavailableException("The stored key has an unexpected format.");
            return decoded.AsSpan(0, written).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(utf8);
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    private static (IntPtr Ptr, int Length) EncodeSecret(byte[] secret)
    {
        var size = Base64.GetMaxEncodedToUtf8Length(secret.Length);
        var buffer = new byte[size + 1]; // trailing NUL
        var ptr = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Base64.EncodeToUtf8(secret, buffer, out _, out _);
            Marshal.Copy(buffer, 0, ptr, buffer.Length);
            return (ptr, buffer.Length);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static void ZeroNative(IntPtr ptr, int length)
    {
        for (var i = 0; i < length; i++) Marshal.WriteByte(ptr, i, 0);
    }

    private static IntPtr BuildSchema()
    {
        var schema = Marshal.AllocHGlobal(SchemaSize); // lives for the process lifetime
        for (var i = 0; i < SchemaSize; i += 8) Marshal.WriteInt64(schema, i, 0);
        Marshal.WriteIntPtr(schema, 0, Marshal.StringToCoTaskMemUTF8(SchemaName));
        Marshal.WriteInt32(schema, 8, 0); // SECRET_SCHEMA_NONE
        string[] names = ["db", "key"];
        for (var i = 0; i < names.Length; i++)
        {
            var offset = SchemaAttributesOffset + i * SchemaAttributeStride;
            Marshal.WriteIntPtr(schema, offset, Marshal.StringToCoTaskMemUTF8(names[i]));
            Marshal.WriteInt32(schema, offset + 8, 0); // SECRET_SCHEMA_ATTRIBUTE_STRING
        }
        return schema;
    }

    /// <summary>A GHashTable of string attributes (db, key) whose keys/values are owned by this wrapper.</summary>
    private sealed class AttributeTable : IDisposable
    {
        private readonly IntPtr[] _strings;
        public IntPtr Handle { get; private set; }

        public AttributeTable(string databaseId, string keyName)
        {
            ArgumentException.ThrowIfNullOrEmpty(databaseId);
            ArgumentException.ThrowIfNullOrEmpty(keyName);
            _strings =
            [
                Marshal.StringToCoTaskMemUTF8("db"), Marshal.StringToCoTaskMemUTF8(databaseId),
                Marshal.StringToCoTaskMemUTF8("key"), Marshal.StringToCoTaskMemUTF8(keyName),
            ];
            try
            {
                var glib = GlibHandle.Value;
                Handle = g_hash_table_new(NativeLibrary.GetExport(glib, "g_str_hash"),
                    NativeLibrary.GetExport(glib, "g_str_equal"));
                g_hash_table_insert(Handle, _strings[0], _strings[1]);
                g_hash_table_insert(Handle, _strings[2], _strings[3]);
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                Dispose();
                throw new SecretStoreUnavailableException("glib is not available on this system.", e);
            }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) { g_hash_table_unref(Handle); Handle = IntPtr.Zero; }
            foreach (var s in _strings) if (s != IntPtr.Zero) Marshal.FreeCoTaskMem(s);
        }
    }

    private static readonly Lazy<IntPtr> GlibHandle = new(() => NativeLibrary.Load(GlibLib));

    [DllImport(SecretLib)]
    private static extern int secret_password_storev_sync(IntPtr schema, IntPtr attributes, IntPtr collection,
        IntPtr label, IntPtr password, IntPtr cancellable, out IntPtr error);

    [DllImport(SecretLib)]
    private static extern IntPtr secret_password_lookupv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable,
        out IntPtr error);

    [DllImport(SecretLib)]
    private static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable,
        out IntPtr error);

    [DllImport(SecretLib)]
    private static extern void secret_password_free(IntPtr password);

    [DllImport(GlibLib)]
    private static extern IntPtr g_hash_table_new(IntPtr hashFunc, IntPtr equalFunc);

    [DllImport(GlibLib)]
    private static extern void g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);

    [DllImport(GlibLib)]
    private static extern void g_hash_table_unref(IntPtr table);

    [DllImport(GlibLib)]
    private static extern void g_error_free(IntPtr error);
}
