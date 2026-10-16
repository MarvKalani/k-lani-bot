using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using KLaniBot.Credentials;

namespace KLaniBot.Backup;

// AES-256-GCM authenticates the bounded archive and its identity; keys stay in the separately scoped OS credential provider.
internal static class BackupCipher
{
    public const int KeyBytes = 32;
    public const int MaximumArchiveBytes = 40 * 1024 * 1024;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static byte[] Encrypt(FileBackupPayload payload, SecretReference reference, ReadOnlySpan<byte> key)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        try
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceBytes);
            byte[] tag = new byte[TagBytes];
            byte[] ciphertext = new byte[plain.Length];
            using var cipher = new AesGcm(key, TagBytes);
            cipher.Encrypt(nonce, plain, ciphertext, tag, AssociatedData(payload.Id, reference));

            return JsonSerializer.SerializeToUtf8Bytes(new BackupEnvelope(1, payload.Id, reference, nonce, tag, ciphertext), JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    // Throws BackupFault or JsonException for invalid envelopes; this check runs before resolving the named credential.
    public static BackupEnvelope ReadEnvelope(byte[] bytes, Guid id)
    {
        var envelope = JsonSerializer.Deserialize<BackupEnvelope>(bytes, JsonOptions);
        FileBackupSource.Require(envelope != null && envelope.Format == 1 && envelope.Id == id
            && CredentialBroker.ValidReference(envelope.Key) && envelope.Nonce?.Length == NonceBytes
            && envelope.Tag?.Length == TagBytes && envelope.Ciphertext?.Length is > 0 and <= MaximumArchiveBytes,
            BackupFailure.InvalidArchive);

        return envelope!;
    }

    // Throws CryptographicException or JsonException when ciphertext, associated metadata or the decrypted representation was changed.
    public static FileBackupPayload Decrypt(BackupEnvelope envelope, ReadOnlySpan<byte> key)
    {
        byte[] plain = new byte[envelope.Ciphertext.Length];
        try
        {
            using var cipher = new AesGcm(key, TagBytes);
            cipher.Decrypt(envelope.Nonce, envelope.Ciphertext, envelope.Tag, plain, AssociatedData(envelope.Id, envelope.Key));
            var payload = JsonSerializer.Deserialize<FileBackupPayload>(plain, JsonOptions);
            FileBackupSource.Require(payload != null, BackupFailure.InvalidArchive);

            return payload!;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static byte[] AssociatedData(Guid id, SecretReference key)
    {
        return Encoding.UTF8.GetBytes(FormattableString.Invariant($"k-lani-bot backup/1:{id:D}:{key.Id:D}:{key.Version}"));
    }
}
