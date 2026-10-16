using System.Security.Cryptography;

namespace KLaniBot.Credentials;

// A trusted operation owns a short-lived byte buffer; disposal erases it and serialization reveals no secret material.
public sealed class SecretLease : IDisposable
{
    private byte[] _bytes;

    internal SecretLease(byte[] ownedBytes)
    {
        _bytes = ownedBytes;
    }

    public int Length => _bytes.Length;
    internal ReadOnlyMemory<byte> Bytes => _bytes;

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_bytes);
        _bytes = [];
    }
}
