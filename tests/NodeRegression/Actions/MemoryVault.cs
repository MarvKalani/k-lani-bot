using System.Security.Cryptography;
using KLaniBot.Credentials;

// A vault that keeps each provisioned key by its reference, like the platform Secret Service does.
internal sealed class MemoryVault : ISecretProvider, IDisposable
{
    private readonly Dictionary<SecretReference, byte[]> _keys = new();

    public bool RefuseWrites
    {
        get; set;
    }

    public bool TryRead(SecretReference reference, out SecretLease? lease, out CredentialFailure failure)
    {
        bool found = _keys.TryGetValue(reference, out byte[]? key);
        lease = found ? new SecretLease(key!.ToArray()) : null;
        failure = found ? CredentialFailure.None : CredentialFailure.NotFound;
        return found;
    }

    public bool TryWrite(SecretReference reference, ReadOnlySpan<byte> value, out CredentialFailure failure)
    {
        if (RefuseWrites)
        {
            failure = CredentialFailure.Denied;
            return false;
        }
        _keys[reference] = value.ToArray();
        failure = CredentialFailure.None;
        return true;
    }

    public bool TryDelete(SecretReference reference, out CredentialFailure failure)
    {
        failure = _keys.Remove(reference) ? CredentialFailure.None : CredentialFailure.NotFound;
        return failure == CredentialFailure.None;
    }

    public void Dispose()
    {
        foreach (byte[] key in _keys.Values)
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
