using System.Text.Json.Nodes;
using KLaniBot.Backup;

namespace KLaniBot.Actions.Handlers;

// Use case: an administrator's AI plans a backup of the folder the operator configured and checks stored archives before
// it changes data. Folders and key come from the node policy; the answers carry counts and fingerprints, never file content.
internal sealed class BackupActionHandlers
{
    private readonly HandlerServices _services;

    public BackupActionHandlers(HandlerServices services)
    {
        _services = services;
    }

    public void Register(Action<Tool, ActionHandler> add)
    {
        add(Tool.BackupPlan, Plan);
        add(Tool.BackupVerify, Verify);
        add(Tool.BackupCreate, Create);
        add(Tool.BackupRestore, Restore);
    }

    private object Plan(ActionCall call)
    {
        FileBackupService service = _services.Backup.Open(call.Caller);

        if (!service.TryPlan(BackupSourceKind.ClosedFiles, out BackupPlan? plan, out BackupFailure failure))
        {
            return BackupAnswers.Failed(failure);
        }
        return BackupAnswers.Planned(plan!);
    }

    // Throws ArgumentException when backup_id or fingerprint is malformed. The plan is taken again on the node, so the
    // client cannot name files: only a source that still has the planned fingerprint is archived.
    private object Create(ActionCall call)
    {
        Guid id = BackupAnswers.ReadId(call.Arguments["backup_id"]?.ToString());
        string fingerprint = BackupAnswers.ReadFingerprint(call.Arguments["fingerprint"]?.ToString());
        FileBackupService service = _services.Backup.Open(call.Caller);

        if (!service.TryPlan(BackupSourceKind.ClosedFiles, out BackupPlan? current, out BackupFailure failure))
        {
            return BackupAnswers.Failed(failure);
        }
        if (current!.Fingerprint != fingerprint)
        {
            return BackupAnswers.Failed(BackupFailure.SourceChanged);
        }
        call.MarkBackendMayHaveMutated();
        if (!service.TryCreate(current with
        {
            Id = id
        }, _services.Backup.Key, out BackupReceipt? receipt, out failure))
        {
            return BackupAnswers.Failed(failure);
        }
        _services.Backup.RecordLatest(receipt!);
        return BackupAnswers.Received(receipt!);
    }

    // Throws ArgumentException when backup_id is no backup identifier.
    private object Restore(ActionCall call)
    {
        Guid id = BackupAnswers.ReadId(call.Arguments["backup_id"]?.ToString());
        string name = call.Arguments["name"]?.ToString() ?? "";
        FileBackupService service = _services.Backup.Open(call.Caller);

        call.MarkBackendMayHaveMutated();
        if (!service.TryRestore(id, name, out BackupRestoreResult? restored, out BackupFailure failure))
        {
            return BackupAnswers.Failed(failure);
        }
        return BackupAnswers.Restored(id, name, restored!);
    }

    // Throws ArgumentException when backup_id is no backup identifier.
    private object Verify(ActionCall call)
    {
        Guid id = BackupAnswers.ReadId(call.Arguments["backup_id"]?.ToString());
        FileBackupService service = _services.Backup.Open(call.Caller);

        if (!service.TryVerify(id, out BackupReceipt? receipt, out BackupFailure failure))
        {
            return BackupAnswers.Failed(failure);
        }
        TimeSpan maximumAge = _services.Backup.Settings!.MaximumAge;
        bool current = service.TryCheckCurrent(id, maximumAge, out BackupFailure staleness);
        JsonObject answer = BackupAnswers.Received(receipt!);
        answer["current"] = current;
        if (!current)
        {
            answer["not_current"] = BackupAnswers.WireName(staleness);
        }
        return answer;
    }
}
