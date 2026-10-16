using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KLaniBot.Credentials;

// Credential access is tested independently of an OS vault; a separate private-session probe qualifies the native provider.
internal static class CredentialCases
{
    public static int Run()
    {
        var checks = new Checks("Credentials");
        var provider = new TestProvider();
        var reference = new SecretReference(Guid.CreateVersion7(), 1);
        var scope = new CredentialUse("node", "principal", "application", CredentialPurpose.ApplicationLogin);
        var grants = new Dictionary<SecretReference, CredentialGrant>
        {
            [reference] = new(reference, scope, DateTimeOffset.UtcNow.AddMinutes(1), CredentialState.Active)
        };
        var broker = new CredentialBroker(provider, grants);
        checks.That(provider.TryWrite(reference, Encoding.UTF8.GetBytes("synthetic-secret"), out _), "fixture secret stored");
        CheckLease(checks, broker, reference, scope);
        checks.That(!broker.TryAcquire(reference, scope with
        {
            Principal = "other"
        }, out _, out var failure)
            && failure == CredentialFailure.Denied, "another principal is refused");
        checks.That(!broker.TryAcquire(reference, scope with
        {
            Purpose = CredentialPurpose.BackupEncryption
        }, out _, out _),
            "another purpose is refused");
        grants[reference] = grants[reference] with
        {
            State = CredentialState.Revoked
        };
        checks.That(!broker.TryAcquire(reference, scope, out _, out _), "revocation takes effect without cached plaintext");
        grants[reference] = grants[reference] with
        {
            State = CredentialState.Active,
            ExpiresAt = DateTimeOffset.UnixEpoch
        };
        checks.That(!broker.TryAcquire(reference, scope, out _, out failure) && failure == CredentialFailure.Expired,
            "expired grants are refused");

        return checks.Passed;
    }

    private static void CheckLease(Checks checks, CredentialBroker broker, SecretReference reference, CredentialUse scope)
    {
        checks.That(broker.TryAcquire(reference, scope, out var lease, out _) && lease != null, "matching scope resolves a lease");
        ReadOnlyMemory<byte> retained = lease!.Bytes;
        using (lease)
        {
            checks.That(lease.Bytes.Span.SequenceEqual(Encoding.UTF8.GetBytes("synthetic-secret")), "value reaches trusted consumer");
            checks.That(!JsonSerializer.Serialize(lease).Contains("synthetic-secret", StringComparison.Ordinal),
                "serialization cannot expose credential bytes");
        }
        checks.That(lease.Length == 0 && retained.ToArray().All(value => value == 0),
            "disposal erases the owned buffer even for a previously retained memory view");
    }

    // Unit tests own this ephemeral value; production never falls back from the OS vault to this implementation.
    private sealed class TestProvider : ISecretProvider
    {
        private readonly Dictionary<SecretReference, byte[]> _values = new();

        public bool TryWrite(SecretReference reference, ReadOnlySpan<byte> value, out CredentialFailure failure)
        {
            _values[reference] = value.ToArray();
            failure = CredentialFailure.None;
            return true;
        }

        public bool TryRead(SecretReference reference, out SecretLease? lease, out CredentialFailure failure)
        {
            lease = _values.TryGetValue(reference, out var value) ? new SecretLease(value.ToArray()) : null;
            failure = lease == null ? CredentialFailure.NotFound : CredentialFailure.None;
            return lease != null;
        }

        public bool TryDelete(SecretReference reference, out CredentialFailure failure)
        {
            if (_values.Remove(reference, out var value))
            {
                CryptographicOperations.ZeroMemory(value);
            }
            failure = CredentialFailure.None;
            return true;
        }
    }
}
