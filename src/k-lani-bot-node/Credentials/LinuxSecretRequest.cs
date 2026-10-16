using System.Globalization;
using System.Runtime.InteropServices;

namespace KLaniBot.Credentials;

// One provider operation owns its exact attribute scope and cancellable, preventing native waits from blocking a UI indefinitely.
internal sealed class LinuxSecretRequest : IDisposable
{
    private readonly List<IntPtr> _strings = [];
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(10));
    private CancellationTokenRegistration _cancellation;
    private IntPtr _glib;
    public IntPtr Attributes
    {
        get; private set;
    }
    public IntPtr Cancellation
    {
        get; private set;
    }
    public bool Cancelled => _timeout.IsCancellationRequested;

    // Rethrows Exception on failed native loading or allocation after cleanup; the provider reports a non-secret failure.
    public LinuxSecretRequest(string scope, SecretReference reference)
    {
        try
        {
            _glib = NativeLibrary.Load("libglib-2.0.so.0");
            Attributes = LinuxSecretNative.g_hash_table_new(NativeLibrary.GetExport(_glib, "g_str_hash"),
                NativeLibrary.GetExport(_glib, "g_str_equal"));
            Add("application", "k-lani-bot");
            Add("scope", scope);
            Add("reference", reference.Id.ToString("D"));
            Add("revision", reference.Version.ToString(CultureInfo.InvariantCulture));
            Cancellation = LinuxSecretNative.g_cancellable_new();
            _cancellation = _timeout.Token.Register(() => LinuxSecretNative.g_cancellable_cancel(Cancellation));
        }
        catch (Exception)
        {
            Dispose();
            throw;
        }
    }

    private void Add(string key, string value)
    {
        IntPtr nativeKey = Marshal.StringToCoTaskMemUTF8(key);
        _strings.Add(nativeKey);
        IntPtr nativeValue = Marshal.StringToCoTaskMemUTF8(value);
        _strings.Add(nativeValue);
        LinuxSecretNative.g_hash_table_insert(Attributes, nativeKey, nativeValue);
    }

    public CredentialFailure ReadFailure(IntPtr error)
    {
        if (error != IntPtr.Zero)
        {
            LinuxSecretNative.g_error_free(error);
        }

        return Cancelled ? CredentialFailure.Cancelled : error == IntPtr.Zero ? CredentialFailure.None : CredentialFailure.ProviderFailed;
    }

    public void Dispose()
    {
        _cancellation.Dispose();
        _timeout.Dispose();
        if (Cancellation != IntPtr.Zero)
        {
            LinuxSecretNative.g_object_unref(Cancellation);
            Cancellation = IntPtr.Zero;
        }
        if (Attributes != IntPtr.Zero)
        {
            LinuxSecretNative.g_hash_table_unref(Attributes);
            Attributes = IntPtr.Zero;
        }
        foreach (IntPtr value in _strings)
        {
            Marshal.FreeCoTaskMem(value);
        }
        _strings.Clear();
        if (_glib != IntPtr.Zero)
        {
            NativeLibrary.Free(_glib);
            _glib = IntPtr.Zero;
        }
    }
}
