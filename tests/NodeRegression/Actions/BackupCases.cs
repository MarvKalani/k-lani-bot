using System.Security.Cryptography;
using KLaniBot.Backup;
using KLaniBot.Credentials;

// File snapshots are checked against restored bytes, stale source changes, wrong keys and archive tampering.
internal static class BackupCases
{
    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Backups");
        string root = Path.Combine(temporaryRoot, "backup-cases");
        var locations = new BackupLocations(Path.Combine(root, "source"), Path.Combine(root, "archives"), Path.Combine(root, "restored"));
        Directory.CreateDirectory(locations.Source);
        File.WriteAllText(Path.Combine(locations.Source, "settings.txt"), "configuration before installation");
        File.WriteAllBytes(Path.Combine(locations.Source, "content.bin"), [0, 1, 255, 12]);
        var reference = new SecretReference(Guid.CreateVersion7(), 1);
        var use = new CredentialUse("node", "admin", "backup", CredentialPurpose.BackupEncryption);
        var grants = new Dictionary<SecretReference, CredentialGrant>
        {
            [reference] = new(reference, use, DateTimeOffset.MaxValue, CredentialState.Active)
        };
        var provider = new BackupKeys();
        var broker = new CredentialBroker(provider, grants);
        var service = new FileBackupService(locations, broker, use);
        CheckRoundTrip(checks, service, locations, reference);
        CheckRejections(checks, service, locations, reference, provider);
        CheckFreshness(checks, locations, broker, use, reference);
        CheckScope(checks, locations, broker, use);
        provider.Dispose();

        return checks.Passed;
    }

    private static void CheckFreshness(Checks checks, BackupLocations locations, CredentialBroker broker,
        CredentialUse use, SecretReference reference)
    {
        var clock = new BackupClock();
        var service = new FileBackupService(locations, broker, use, clock);
        checks.That(service.TryPlan(BackupSourceKind.ClosedFiles, out var plan, out _), "freshness plan");
        checks.That(service.TryCreate(plan!, reference, out var receipt, out _), "freshness archive");
        clock.Now = clock.Now.AddHours(2);
        checks.That(service.TryVerify(receipt!.Id, out var verified, out _) && verified!.CapturedAt == receipt.CapturedAt,
            "verification does not renew the original capture time");
        checks.That(!service.TryCheckCurrent(receipt.Id, TimeSpan.FromHours(1), out var failure)
            && failure == BackupFailure.Expired, "expired evidence cannot authorize a new mutation");
    }

    private static void CheckScope(Checks checks, BackupLocations locations, CredentialBroker broker, CredentialUse use)
    {
        var overlapping = locations with
        {
            Archives = Path.Combine(locations.Source, "backups")
        };
        checks.That(!new FileBackupService(overlapping, broker, use).TryPlan(BackupSourceKind.ClosedFiles, out _, out var failure)
            && failure == BackupFailure.InvalidScope, "an archive cannot recursively capture itself");
        checks.That(!BackupPaths.ValidRelativeFile("CON.txt") && !BackupPaths.ValidRelativeFile("folder/LPT1")
            && !BackupPaths.ValidDestination("NUL") && !BackupPaths.ValidRelativeFile("file."),
            "recovery paths cannot become Windows devices or normalized aliases");
        if (!OperatingSystem.IsWindows())
        {
            string link = Path.Combine(locations.Source, "linked.txt");
            File.CreateSymbolicLink(link, Path.Combine(locations.Source, "settings.txt"));
            checks.That(!new FileBackupService(locations, broker, use).TryPlan(BackupSourceKind.ClosedFiles, out _, out failure)
                && failure == BackupFailure.InvalidScope, "source links are refused before any capture");
            File.Delete(link);
        }
    }

    // Backup freshness is tested without waiting on wall time or changing the host clock.
    private sealed class BackupClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    private static void CheckRoundTrip(Checks checks, FileBackupService service, BackupLocations locations, SecretReference key)
    {
        checks.That(service.TryPlan(BackupSourceKind.ClosedFiles, out var plan, out var failure), "plan: " + failure);
        checks.That(service.TryCreate(plan!, key, out var receipt, out failure), "create: " + failure);
        checks.That(receipt!.RestoreVerified && receipt.Files == 2, "creation includes an actual restore qualification");
        checks.That(service.TryCheckCurrent(receipt.Id, TimeSpan.FromHours(1), out failure), "current backup authorizes matching scope");
        checks.That(service.TryRestore(receipt.Id, "copy", out var restored, out failure), "restore: " + failure);
        checks.That(File.ReadAllText(Path.Combine(restored!.Directory, "settings.txt")) == "configuration before installation",
            "text restored exactly");
        checks.That(File.ReadAllBytes(Path.Combine(restored.Directory, "content.bin")).SequenceEqual(new byte[] { 0, 1, 255, 12 }),
            "binary data restored exactly");
        checks.That(!service.TryRestore(receipt.Id, "copy", out _, out failure) && failure == BackupFailure.DestinationExists,
            "restore never overwrites an existing directory");
        string archive = Directory.GetFiles(locations.Archives, "*.klbackup").Single();
        checks.That(!File.ReadAllText(archive).Contains("configuration before installation", StringComparison.Ordinal),
            "archive contains no plaintext source contents");
        File.WriteAllText(Path.Combine(locations.Source, "settings.txt"), "changed after snapshot");
        checks.That(!service.TryCheckCurrent(receipt.Id, TimeSpan.FromHours(1), out failure)
            && failure == BackupFailure.SourceChanged, "changed source invalidates the old backup gate");
    }

    private static void CheckRejections(Checks checks, FileBackupService service, BackupLocations locations,
        SecretReference key, BackupKeys provider)
    {
        checks.That(!service.TryPlan(BackupSourceKind.ApplicationExport, out _, out var failure)
            && failure == BackupFailure.UnsupportedSource, "unimplemented application exports are not claimed as consistent backups");
        File.WriteAllText(Path.Combine(locations.Source, "open.sqlite"), "not a qualified database export");
        checks.That(!service.TryPlan(BackupSourceKind.ClosedFiles, out _, out failure), "database files require their own export adapter");
        File.Delete(Path.Combine(locations.Source, "open.sqlite"));
        checks.That(service.TryPlan(BackupSourceKind.ClosedFiles, out var plan, out failure), "new plan after source change");
        provider.Available = false;
        checks.That(!service.TryCreate(plan!, key, out _, out failure) && failure == BackupFailure.MissingKey,
            "missing key never falls back to plaintext backup");
        provider.Available = true;
        var archive = Directory.GetFiles(locations.Archives, "*.klbackup").Single();
        byte[] bytes = File.ReadAllBytes(archive);
        bytes[bytes.Length / 2] ^= 1;
        File.WriteAllBytes(archive, bytes);
        Guid id = Guid.Parse(Path.GetFileNameWithoutExtension(archive));
        checks.That(!service.TryVerify(id, out _, out failure), "tampered ciphertext or envelope cannot verify");
        checks.That(!service.TryRestore(id, "../escape", out _, out _), "restore cannot escape its configured root");
    }

    // Synthetic keys remain ephemeral; the production service receives its key through the scoped credential broker.
    private sealed class BackupKeys : ISecretProvider, IDisposable
    {
        private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);
        public bool Available { get; set; } = true;

        public bool TryRead(SecretReference reference, out SecretLease? lease, out CredentialFailure failure)
        {
            lease = Available ? new SecretLease(_key.ToArray()) : null;
            failure = Available ? CredentialFailure.None : CredentialFailure.Unavailable;
            return Available;
        }

        public bool TryWrite(SecretReference reference, ReadOnlySpan<byte> value, out CredentialFailure failure)
        {
            failure = CredentialFailure.Denied;
            return false;
        }

        public bool TryDelete(SecretReference reference, out CredentialFailure failure)
        {
            Available = false;
            failure = CredentialFailure.None;
            return true;
        }

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(_key);
        }
    }
}
