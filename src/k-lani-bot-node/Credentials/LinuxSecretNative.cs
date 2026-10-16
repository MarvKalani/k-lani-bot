using System.Runtime.InteropServices;

namespace KLaniBot.Credentials;

// The optional Linux provider uses libsecret 0.19 binary values; native libraries remain OS dependencies, not model runtimes.
internal static class LinuxSecretNative
{
    private const string ServiceLibraryName = "libsecret-1.so.0";
    private const string GlibLibrary = "libglib-2.0.so.0";
    private const string GioLibrary = "libgio-2.0.so.0";

    [DllImport(ServiceLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr secret_value_new(byte[] value, nint length, [MarshalAs(UnmanagedType.LPUTF8Str)] string contentType);
    [DllImport(ServiceLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr secret_value_get(IntPtr value, out nuint length);
    [DllImport(ServiceLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void secret_value_unref(IntPtr value);
    [DllImport(ServiceLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int secret_password_storev_binary_sync(IntPtr schema, IntPtr attributes, IntPtr collection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string label, IntPtr value, IntPtr cancellable, out IntPtr error);
    [DllImport(ServiceLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr secret_password_lookupv_binary_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable,
        out IntPtr error);
    [DllImport(ServiceLibraryName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int secret_password_clearv_sync(IntPtr schema, IntPtr attributes, IntPtr cancellable, out IntPtr error);
    [DllImport(GlibLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr g_hash_table_new(IntPtr hash, IntPtr equal);
    [DllImport(GlibLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int g_hash_table_insert(IntPtr table, IntPtr key, IntPtr value);
    [DllImport(GlibLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void g_hash_table_unref(IntPtr table);
    [DllImport(GlibLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void g_error_free(IntPtr error);
    [DllImport(GioLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr g_cancellable_new();
    [DllImport(GioLibrary, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void g_cancellable_cancel(IntPtr cancellable);
    [DllImport("libgobject-2.0.so.0", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void g_object_unref(IntPtr value);
}
