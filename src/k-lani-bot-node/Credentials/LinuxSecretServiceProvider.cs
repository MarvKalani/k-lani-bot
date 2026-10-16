using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace KLaniBot.Credentials;

// Trusted Linux node code stores scoped binary secrets in the user's Secret Service; unavailable vaults have no file fallback.
public sealed class LinuxSecretServiceProvider(string scope) : ISecretProvider
{
    public const int MaximumSecretBytes = 4096;

    public bool TryWrite(SecretReference reference, ReadOnlySpan<byte> value, out CredentialFailure failure)
    {
        failure = CredentialFailure.InvalidValue;
        if (!TryReference(reference, out failure) || value.Length is < 1 or > MaximumSecretBytes)
        {
            failure = failure == CredentialFailure.None ? CredentialFailure.InvalidValue : failure;
            return false;
        }
        byte[] copy = [];
        IntPtr nativeValue = IntPtr.Zero;
        try
        {
            copy = value.ToArray();
            using var request = new LinuxSecretRequest(scope, reference);
            nativeValue = LinuxSecretNative.secret_value_new(copy, copy.Length, "application/octet-stream");
            failure = StoreValue(request, nativeValue);
            return failure == CredentialFailure.None;
        }
        catch (Exception exception)
        {
            failure = Failure(exception);
            return false;
        }
        finally
        {
            if (nativeValue != IntPtr.Zero)
            {
                LinuxSecretNative.secret_value_unref(nativeValue);
            }
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private static CredentialFailure StoreValue(LinuxSecretRequest request, IntPtr value)
    {
        if (value == IntPtr.Zero)
        {
            return CredentialFailure.ProviderFailed;
        }
        int stored = LinuxSecretNative.secret_password_storev_binary_sync(IntPtr.Zero, request.Attributes, IntPtr.Zero,
            "k-lani-bot credential", value, request.Cancellation, out var error);
        var failure = request.ReadFailure(error);

        return stored == 0 && failure == CredentialFailure.None ? CredentialFailure.ProviderFailed : failure;
    }

    public bool TryRead(SecretReference reference, out SecretLease? lease, out CredentialFailure failure)
    {
        lease = null;
        if (!TryReference(reference, out failure))
        {
            return false;
        }
        IntPtr nativeValue = IntPtr.Zero;
        try
        {
            using var request = new LinuxSecretRequest(scope, reference);
            nativeValue = LinuxSecretNative.secret_password_lookupv_binary_sync(IntPtr.Zero, request.Attributes,
                request.Cancellation, out var error);
            failure = request.ReadFailure(error);
            if (failure != CredentialFailure.None)
            {
                return false;
            }
            return TryCopy(nativeValue, out lease, out failure);
        }
        catch (Exception exception)
        {
            failure = Failure(exception);
            return false;
        }
        finally
        {
            if (nativeValue != IntPtr.Zero)
            {
                LinuxSecretNative.secret_value_unref(nativeValue);
            }
        }
    }

    public bool TryDelete(SecretReference reference, out CredentialFailure failure)
    {
        if (!TryReference(reference, out failure))
        {
            return false;
        }
        try
        {
            using var request = new LinuxSecretRequest(scope, reference);
            LinuxSecretNative.secret_password_clearv_sync(IntPtr.Zero, request.Attributes, request.Cancellation, out var error);
            failure = request.ReadFailure(error);

            return failure == CredentialFailure.None;
        }
        catch (Exception exception)
        {
            failure = Failure(exception);
            return false;
        }
    }

    private bool TryReference(SecretReference reference, out CredentialFailure failure)
    {
        failure = CredentialFailure.InvalidReference;
        if (!CredentialBroker.ValidReference(reference) || string.IsNullOrWhiteSpace(scope) || scope.Length > 256
            || scope.Any(char.IsControl))
        {
            return false;
        }
        failure = CredentialFailure.Unavailable;
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
        {
            return false;
        }
        failure = CredentialFailure.None;

        return true;
    }

    private static bool TryCopy(IntPtr nativeValue, out SecretLease? lease, out CredentialFailure failure)
    {
        lease = null;
        failure = CredentialFailure.NotFound;
        if (nativeValue == IntPtr.Zero)
        {
            return false;
        }
        IntPtr data = LinuxSecretNative.secret_value_get(nativeValue, out var length);
        if (data == IntPtr.Zero || length is < 1 or > MaximumSecretBytes)
        {
            failure = CredentialFailure.InvalidValue;
            return false;
        }
        byte[] bytes = new byte[(int)length];
        Marshal.Copy(data, bytes, 0, bytes.Length);
        lease = new(bytes);
        failure = CredentialFailure.None;

        return true;
    }

    private static CredentialFailure Failure(Exception exception)
    {
        return exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
            ? CredentialFailure.Unavailable : CredentialFailure.ProviderFailed;
    }
}
