using KLaniBot.Actions;
using KLaniBot.Guard;

namespace KLaniBot.Backup;

// Use case: before the first change of a session the dispatcher asks here whether a current backup exists. It proves that
// the last archive of the configured source matches the source as it is right before the change; later steps of the same
// session change the source as expected and stay covered until the archive is older than the operator's maximum age.
// It does not prove that the data a particular change touches lies inside that source.
internal sealed class BackupGate(NodePolicy policy, NodeBackup backup, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _coveredUntil = new(StringComparer.Ordinal);

    // Changes that create or restore the backup itself, or only end the session, cannot depend on a backup.
    public static bool Exempt(Tool tool)
    {
        return tool is Tool.BackupCreate or Tool.BackupRestore or Tool.EndSession or Tool.SaveMacro or Tool.RunMacro;
    }

    // Throws BackupRequiredException when the requirement is on and no current archive covers this session, and
    // UnauthorizedAccessException when the policy became unreadable.
    public void Require(RequestContext caller, Tool tool)
    {
        if (Exempt(tool) || policy.CurrentBackupRequirement() == BackupRequirement.Off)
        {
            return;
        }
        DateTimeOffset now = _clock.GetUtcNow();
        if (_coveredUntil.TryGetValue(caller.SessionId, out DateTimeOffset until) && until > now)
        {
            return;
        }

        BackupPolicy settings = policy.Backup ?? throw new BackupRequiredException(
            "A current backup is required before changes, and this node has no backup section. Ask the administrator to "
            + "configure backup on this node or to switch the requirement off.");
        NodeBackupLatest latest = backup.ReadLatest() ?? throw new BackupRequiredException(
            "A current backup is required before changes. Run backup_plan and backup_create first.");
        if (!backup.Open(caller).TryCheckCurrent(latest.Id, settings.MaximumAge, out BackupFailure failure))
        {
            string reason = Actions.Handlers.BackupAnswers.WireName(failure);
            throw new BackupRequiredException("The last backup does not cover this change (" + reason
                + "). Run backup_plan and backup_create again; the backup covers only the configured source.");
        }
        RemoveExpired(now);
        _coveredUntil[caller.SessionId] = latest.CapturedAt + settings.MaximumAge;
    }

    // A long-running actuator sees many sessions; covers that ran out are of no use and are dropped.
    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (string session in _coveredUntil.Where(entry => entry.Value <= now).Select(entry => entry.Key).ToArray())
        {
            _coveredUntil.Remove(session);
        }
    }
}

// The dispatcher refuses the change before it starts and answers with the code backup_required.
public sealed class BackupRequiredException(string message) : InvalidOperationException(message);
