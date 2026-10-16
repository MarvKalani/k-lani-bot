using KLaniBot.Credentials;
using KLaniBot.Backup;
using KLaniBot.Common;
using System.Security.Cryptography;

// Only an explicitly isolated bus and empty test keyring qualify the OS provider; the operator's keyring is never a fixture.
internal static class CredentialNativeCases
{
    public static int Run()
    {
        if (Environment.GetEnvironmentVariable("K_LANI_SECRET_PROBE") != "isolated")
        {
            throw new InvalidOperationException("Run the credential probe only in its isolated keyring session.");
        }
        var checks = new Checks("Native credential provider");
        var reference = new SecretReference(Guid.CreateVersion7(), 1);
        string scope = "fixture-" + Guid.CreateVersion7();
        var provider = new LinuxSecretServiceProvider(scope);
        byte[] secret = [0, 1, 2, 255, 127, 42];
        try
        {
            checks.That(provider.TryWrite(reference, secret, out var failure), "native store: " + failure);
            checks.That(new LinuxSecretServiceProvider(scope).TryRead(reference, out var lease, out failure),
                "native lookup after provider restart: " + failure);
            using (lease)
            {
                checks.That(lease!.Bytes.Span.SequenceEqual(secret), "native binary secret read back exactly");
            }
            checks.That(!new LinuxSecretServiceProvider(scope + "-other").TryRead(reference, out _, out failure)
                && failure == CredentialFailure.NotFound, "another scope cannot resolve the reference");
            checks.That(provider.TryDelete(reference, out failure), "native delete: " + failure);
            checks.That(!provider.TryRead(reference, out _, out failure) && failure == CredentialFailure.NotFound,
                "deleted reference no longer resolves");
            CheckEncryptedBackup(checks, provider);
            CheckProvisionedBackupKey(checks, provider);
        }
        finally
        {
            provider.TryDelete(reference, out _);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(secret);
        }

        return checks.Passed;
    }

    // The key that --create-backup-key stores must be readable by the same vault scope and removable again.
    private static void CheckProvisionedBackupKey(Checks checks, ISecretProvider provider)
    {
        var created = BackupKeyProvisioning.TryCreate(provider, out CredentialFailure failure);
        checks.That(created != null, "native backup key provisioned: " + failure);
        var reference = new SecretReference(Guid.Parse(created!["keyId"]!.ToString()), created["keyVersion"]!.GetValue<int>());
        try
        {
            checks.That(provider.TryRead(reference, out SecretLease? lease, out failure) && lease!.Bytes.Length == 32,
                "provisioned native key reads back with 32 bytes: " + failure);
            lease?.Dispose();
        }
        finally
        {
            provider.TryDelete(reference, out _);
        }
    }

    private static void CheckEncryptedBackup(Checks checks, ISecretProvider provider)
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-native-backup-" + Guid.CreateVersion7());
        var reference = new SecretReference(Guid.CreateVersion7(), 1);
        byte[] key = RandomNumberGenerator.GetBytes(32);
        var use = new CredentialUse("fixture-node", "fixture-admin", "backup", CredentialPurpose.BackupEncryption);
        var grants = new Dictionary<SecretReference, CredentialGrant>
        {
            [reference] = new(reference, use, DateTimeOffset.MaxValue, CredentialState.Active)
        };
        var locations = new BackupLocations(Path.Combine(root, "source"), Path.Combine(root, "archives"), Path.Combine(root, "restores"));
        PrivateFile.CreateDirectory(root);
        Directory.CreateDirectory(locations.Source);
        File.WriteAllText(Path.Combine(locations.Source, "software-settings.txt"), "synthetic settings before an upgrade");
        try
        {
            checks.That(provider.TryWrite(reference, key, out _), "native backup key stored");
            var service = new FileBackupService(locations, new CredentialBroker(provider, grants), use);
            checks.That(service.TryPlan(BackupSourceKind.ClosedFiles, out var plan, out _), "native backup planned");
            checks.That(service.TryCreate(plan!, reference, out var receipt, out var failure), "native encrypted backup: " + failure);
            checks.That(service.TryRestore(receipt!.Id, "copy", out var restored, out failure), "native-key restore: " + failure);
            checks.That(File.ReadAllText(Path.Combine(restored!.Directory, "software-settings.txt"))
                == "synthetic settings before an upgrade", "native-key backup restores the original file");
            provider.TryDelete(reference, out _);
            checks.That(!service.TryVerify(receipt.Id, out _, out failure) && failure == BackupFailure.MissingKey,
                "missing native key blocks decryption without a plaintext fallback");
        }
        finally
        {
            provider.TryDelete(reference, out _);
            CryptographicOperations.ZeroMemory(key);
            Directory.Delete(root, true);
        }
    }
}
