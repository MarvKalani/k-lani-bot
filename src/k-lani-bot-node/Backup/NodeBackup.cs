using KLaniBot.Common;
using KLaniBot.Credentials;
using KLaniBot.Guard;

namespace KLaniBot.Backup;

// Use case: the node's backup tools ask here for a backup service bound to the operator's policy and the authenticated
// caller. The vault key is granted only to that caller, only for backup encryption and only while the caller is valid.
internal sealed class NodeBackup(NodePolicy policy, ISecretProvider? keys, TimeProvider? clock = null)
{
    public const string KeyApplication = "backup";

    private const string LatestFile = "backup-latest.json";

    public BackupPolicy? Settings => policy.Backup;

    // The archive the gate checks: the last one backup_create wrote on this node, kept in the private node state.
    public void RecordLatest(BackupReceipt receipt)
    {
        var latest = new System.Text.Json.Nodes.JsonObject
        {
            ["backup_id"] = receipt.Id.ToString(),
            ["captured_at"] = receipt.CapturedAt
        };
        PrivateFile.WriteAllBytesAtomically(Path.Combine(policy.StateRoot, LatestFile),
            System.Text.Encoding.UTF8.GetBytes(latest.ToJsonString()));
    }

    // Null when no archive was recorded or the record is unreadable; the gate then asks for a new backup.
    public NodeBackupLatest? ReadLatest()
    {
        string path = Path.Combine(policy.StateRoot, LatestFile);
        System.Text.Json.Nodes.JsonObject? latest;
        try
        {
            latest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) as System.Text.Json.Nodes.JsonObject;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException)
        {
            return null;
        }

        if (!Guid.TryParse(latest?["backup_id"]?.ToString(), out Guid id)
            || !DateTimeOffset.TryParse(latest?["captured_at"]?.ToString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset capturedAt))
        {
            return null;
        }
        return new NodeBackupLatest(id, capturedAt);
    }

    // Throws NotSupportedException when the policy has no backup section.
    public SecretReference Key
    {
        get
        {
            BackupPolicy settings = policy.Backup ?? throw new NotSupportedException("Backup is not configured on this node.");
            return new SecretReference(settings.KeyId, settings.KeyVersion);
        }
    }

    // The Secret Service attribute scope that the key provisioning command and the backup tools share.
    public static string VaultScope(string nodeId)
    {
        return "k-lani-bot-backup/" + nodeId;
    }

    // Only the Linux Secret Service is qualified; other systems have no vault here and no plaintext fallback.
    public static ISecretProvider? PlatformKeys(NodePolicy policy)
    {
        return OperatingSystem.IsLinux() ? new LinuxSecretServiceProvider(VaultScope(policy.NodeId)) : null;
    }

    // Throws NotSupportedException when the policy has no backup section or this system has no qualified vault, and
    // UnauthorizedAccessException when the caller expired or was revoked.
    public FileBackupService Open(RequestContext caller)
    {
        BackupPolicy settings = policy.Backup
            ?? throw new NotSupportedException("Backup is not configured on this node; the operator adds a backup section to its policy.");
        ISecretProvider vault = keys
            ?? throw new NotSupportedException("This operating system has no qualified key vault for backups.");
        PrincipalPolicy principal = policy.Get(caller.PrincipalId);

        var use = new CredentialUse(policy.NodeId, principal.Id, KeyApplication, CredentialPurpose.BackupEncryption);
        var key = new SecretReference(settings.KeyId, settings.KeyVersion);
        var grants = new Dictionary<SecretReference, CredentialGrant>
        {
            [key] = new(key, use, principal.ExpiresAt, CredentialState.Active)
        };
        var locations = new BackupLocations(settings.Source, settings.Archives, settings.Restores);

        return new FileBackupService(locations, new CredentialBroker(vault, grants, clock), use, clock);
    }
}

// The last archive written on this node and when its source was captured.
internal sealed record NodeBackupLatest(Guid Id, DateTimeOffset CapturedAt);
