using System.Diagnostics;
using System.Text.Json.Nodes;
using KLaniBot.Backup;

namespace KLaniBot.Actions.Handlers;

// The backup tools answer the AI client with these fields: identifiers, counts, times and a fingerprint, so it can tell
// whether a backup covers the next change without seeing any file name or content.
internal static class BackupAnswers
{
    public static JsonObject Planned(BackupPlan plan)
    {
        return new JsonObject
        {
            ["success"] = true,
            ["backup_id"] = plan.Id.ToString(),
            ["kind"] = "closed_files",
            ["fingerprint"] = plan.Fingerprint,
            ["files"] = plan.Files.Count,
            ["bytes"] = plan.Files.Sum(file => file.Bytes),
            ["planned_at"] = plan.PlannedAt
        };
    }

    public static JsonObject Received(BackupReceipt receipt)
    {
        return new JsonObject
        {
            ["success"] = true,
            ["backup_id"] = receipt.Id.ToString(),
            ["fingerprint"] = receipt.Fingerprint,
            ["files"] = receipt.Files,
            ["bytes"] = receipt.Bytes,
            ["captured_at"] = receipt.CapturedAt,
            ["verified_at"] = receipt.VerifiedAt,
            ["restore_verified"] = receipt.RestoreVerified
        };
    }

    // The recovery folder's location stays with the operator; the client learns the name it chose and what was checked.
    public static JsonObject Restored(Guid id, string name, BackupRestoreResult restored)
    {
        return new JsonObject
        {
            ["success"] = true,
            ["backup_id"] = id.ToString(),
            ["restored_name"] = name,
            ["files"] = restored.Files,
            ["fingerprint"] = restored.Fingerprint
        };
    }

    public static JsonObject Failed(BackupFailure failure)
    {
        return new JsonObject
        {
            ["success"] = false,
            ["failure"] = WireName(failure),
            ["error"] = Explain(failure)
        };
    }

    // Throws ArgumentException when the text is no backup identifier; identifiers are RFC 9562 UUIDv7 values.
    public static Guid ReadId(string? text)
    {
        if (!Guid.TryParse(text, out Guid id) || id.Version != 7)
        {
            throw new ArgumentException("backup_id must be a backup identifier returned by backup_plan or backup_create.");
        }
        return id;
    }

    // Throws ArgumentException when the text is no SHA-256 fingerprint as backup_plan returns it.
    public static string ReadFingerprint(string? text)
    {
        if (text is not { Length: 64 } || !text.All(character => char.IsAsciiHexDigitLower(character) || char.IsAsciiDigit(character)))
        {
            throw new ArgumentException("fingerprint must be the fingerprint returned by backup_plan.");
        }
        return text;
    }

    public static string WireName(BackupFailure failure)
    {
        return failure switch
        {
            BackupFailure.None => "none",
            BackupFailure.UnsupportedSource => "unsupported_source",
            BackupFailure.InvalidScope => "invalid_scope",
            BackupFailure.InvalidPlan => "invalid_plan",
            BackupFailure.SourceChanged => "source_changed",
            BackupFailure.LimitExceeded => "limit_exceeded",
            BackupFailure.MissingKey => "missing_key",
            BackupFailure.StoreFailed => "store_failed",
            BackupFailure.InvalidArchive => "invalid_archive",
            BackupFailure.DestinationExists => "destination_exists",
            BackupFailure.Expired => "expired",
            _ => throw new UnreachableException()
        };
    }

    private static string Explain(BackupFailure failure)
    {
        return failure switch
        {
            BackupFailure.None => "No failure was reported.",
            BackupFailure.UnsupportedSource => "This source kind has no qualified backup method yet.",
            BackupFailure.InvalidScope => "The configured folders overlap, are links or hold files the backup cannot take.",
            BackupFailure.InvalidPlan => "The plan does not belong to this node or is malformed; plan again.",
            BackupFailure.SourceChanged => "Files changed after planning; plan again once the source is quiet.",
            BackupFailure.LimitExceeded => "The source has more or larger files than one backup may hold.",
            BackupFailure.MissingKey => "The backup key is missing, not granted or the vault is unavailable.",
            BackupFailure.StoreFailed => "The archive or recovery folder could not be written or synchronized.",
            BackupFailure.InvalidArchive => "No intact archive with this identifier exists.",
            BackupFailure.DestinationExists => "An archive with this identifier or a folder with this name already exists.",
            BackupFailure.Expired => "The backup is older than the operator's maximum age.",
            _ => throw new UnreachableException()
        };
    }
}
