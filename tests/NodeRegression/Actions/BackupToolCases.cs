using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Backup;
using KLaniBot.Credentials;
using KLaniBot.Guard;

// The backup tools run through the dispatcher like every other tool: they need their scope, a backup section in the
// policy and a vault key, and they answer with counts, times and fingerprints only.
internal static class BackupToolCases
{
    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Backup tools");
        string root = Path.Combine(temporaryRoot, "backup-tools-" + Guid.CreateVersion7().ToString("N"));
        using var vault = new MemoryVault();
        CheckRefusals(checks, root, vault);
        CheckPlanAndVerify(checks, root, vault);
        CheckCreateAndRestore(checks, Path.Combine(root, "changes"), vault);
        CheckScopes(checks, Path.Combine(root, "scopes"), vault);
        CheckKeyProvisioning(checks);

        return checks.Passed;
    }

    private static void CheckRefusals(Checks checks, string root, MemoryVault vault)
    {
        NodePolicy unconfigured = Policy(root, "unconfigured", [Scope.Observe, Scope.BackupRead], null);
        using (var dispatcher = new ActionDispatcher(new FakeUi(), unconfigured, backupKeys: vault))
        {
            JsonObject answer = dispatcher.Execute(Request("backup_plan"), Caller());
            checks.That(answer["code"]?.ToString() == "unsupported", "a node without backup section says backup is unsupported");
            checks.That(answer["error"]?.ToString().Contains("not configured") == true, "the answer names the missing section");
        }
        BackupPolicy settings = Settings(root, vault);
        NodePolicy withoutScope = Policy(root, "without-scope", [Scope.Observe, Scope.FilesRead], settings);
        using (var dispatcher = new ActionDispatcher(new FakeUi(), withoutScope, backupKeys: vault))
        {
            JsonObject answer = dispatcher.Execute(Request("backup_plan"), Caller());
            checks.That(answer["code"]?.ToString() == "denied", "planning a backup needs backup.read");
        }
        NodePolicy configured = Policy(root, "malformed-id", [Scope.Observe, Scope.BackupRead], settings);
        using (var dispatcher = new ActionDispatcher(new FakeUi(), configured, backupKeys: vault))
        {
            var arguments = new JsonObject { ["backup_id"] = "settings.txt" };
            JsonObject answer = dispatcher.Execute(Request("backup_verify", arguments), Caller());
            checks.That(answer["code"]?.ToString() == "invalid_request", "backup_id must be a backup identifier");
        }
    }

    private static void CheckPlanAndVerify(Checks checks, string root, MemoryVault vault)
    {
        BackupPolicy settings = Settings(root, vault);
        Scope[] scopes = [Scope.Observe, Scope.BackupRead, Scope.BackupCreate];
        NodePolicy policy = Policy(root, "plan-verify", scopes, settings);
        RequestContext caller = Caller();
        using var dispatcher = new ActionDispatcher(new FakeUi(), policy, backupKeys: vault);

        JsonObject plan = dispatcher.Execute(Request("backup_plan"), caller);
        checks.That(plan["success"]?.GetValue<bool>() == true && plan["files"]?.GetValue<int>() == 2,
            "the plan counts the two source files");
        checks.That(plan["outcome"]?.ToString() == "completed", "planning ends inside the node");
        checks.That(!plan.ToJsonString().Contains("settings.txt"), "the answer names no source file");
        Guid id = Guid.Parse(plan["backup_id"]!.ToString());
        JsonObject missing = dispatcher.Execute(Request("backup_verify", Identified(id)), caller);
        checks.That(missing["failure"]?.ToString() == "invalid_archive", "a plan is no archive yet");

        JsonObject created = dispatcher.Execute(Request("backup_create", Creation(plan)), caller);
        checks.That(created["success"]?.GetValue<bool>() == true && created["outcome"]?.ToString() == "completed",
            "the planned source is archived and the archive is checked inside the node");
        JsonObject verified = dispatcher.Execute(Request("backup_verify", Identified(id)), caller);
        checks.That(verified["success"]?.GetValue<bool>() == true && verified["restore_verified"]?.GetValue<bool>() == true,
            "a stored archive verifies and records its restore check");
        checks.That(verified["current"]?.GetValue<bool>() == true, "a new archive covers a following change");

        var later = new ShiftedClock(TimeSpan.FromHours(25));
        using var laterDispatcher = new ActionDispatcher(new FakeUi(), policy, clock: later, backupKeys: vault);
        JsonObject aged = laterDispatcher.Execute(Request("backup_verify", Identified(id)), caller);
        checks.That(aged["current"]?.GetValue<bool>() == false && aged["not_current"]?.ToString() == "expired",
            "an archive older than the maximum age verifies but no longer covers a change");
    }

    private static void CheckCreateAndRestore(Checks checks, string root, MemoryVault vault)
    {
        BackupPolicy settings = Settings(root, vault);
        Scope[] scopes = [Scope.Observe, Scope.BackupRead, Scope.BackupCreate, Scope.BackupRestore];
        NodePolicy policy = Policy(root, "create-restore", scopes, settings);
        RequestContext caller = Caller();
        using var dispatcher = new ActionDispatcher(new FakeUi(), policy, backupKeys: vault);
        JsonObject plan = dispatcher.Execute(Request("backup_plan"), caller);
        string requestId = Guid.CreateVersion7().ToString();

        JsonObject first = dispatcher.Execute(Request("backup_create", Creation(plan, requestId)), caller);
        JsonObject retry = dispatcher.Execute(Request("backup_create", Creation(plan, requestId)), caller);
        checks.That(first["success"]?.GetValue<bool>() == true && retry["replayed"]?.GetValue<bool>() == true,
            "a retried request gets the recorded answer");
        checks.That(Directory.GetFiles(settings.Archives, "*.klbackup").Length == 1, "a retry writes no second archive");
        JsonObject again = dispatcher.Execute(Request("backup_create", Creation(plan)), caller);
        checks.That(again["failure"]?.ToString() == "destination_exists", "an existing archive is never replaced");

        var restore = Restoration(plan, "before-change");
        JsonObject restored = dispatcher.Execute(Request("backup_restore", restore), caller);
        string copy = Path.Combine(settings.Restores, "before-change", "settings.txt");
        checks.That(restored["success"]?.GetValue<bool>() == true && File.ReadAllText(copy) == "configuration before the change",
            "the archive restores into a new folder with the original content");
        checks.That(!restored.ToJsonString().Contains(settings.Restores), "the answer does not reveal the recovery folder");
        var existing = Restoration(plan, "before-change");
        JsonObject twice = dispatcher.Execute(Request("backup_restore", existing), caller);
        checks.That(twice["failure"]?.ToString() == "destination_exists", "an existing recovery folder is never overwritten");

        File.WriteAllText(Path.Combine(settings.Source, "settings.txt"), "configuration after the change");
        JsonObject changed = dispatcher.Execute(Request("backup_create", Creation(plan)), caller);
        checks.That(changed["failure"]?.ToString() == "source_changed", "a source changed since planning is not archived");
    }

    private static void CheckScopes(Checks checks, string root, MemoryVault vault)
    {
        BackupPolicy settings = Settings(root, vault);
        NodePolicy readOnly = Policy(root, "read-only", [Scope.Observe, Scope.BackupRead], settings);
        using var dispatcher = new ActionDispatcher(new FakeUi(), readOnly, backupKeys: vault);
        RequestContext caller = Caller();
        JsonObject plan = dispatcher.Execute(Request("backup_plan"), caller);

        JsonObject create = dispatcher.Execute(Request("backup_create", Creation(plan)), caller);
        checks.That(create["code"]?.ToString() == "denied", "archiving needs backup.create");
        var restore = Restoration(plan, "copy");
        JsonObject restored = dispatcher.Execute(Request("backup_restore", restore), caller);
        checks.That(restored["code"]?.ToString() == "denied", "restoring needs backup.restore");
    }

    private static void CheckKeyProvisioning(Checks checks)
    {
        using var vault = new MemoryVault();
        JsonObject? created = BackupKeyProvisioning.TryCreate(vault, out _);
        checks.That(created?.Count == 2 && created["keyVersion"]?.GetValue<int>() == 1, "the answer holds only the key identity");
        var reference = new SecretReference(Guid.Parse(created!["keyId"]!.ToString()), 1);
        bool stored = vault.TryRead(reference, out SecretLease? lease, out _);
        checks.That(reference.Id.Version == 7 && stored && lease!.Bytes.Length == 32, "a 32-byte key is stored under that identity");
        lease?.Dispose();
        vault.RefuseWrites = true;
        checks.That(BackupKeyProvisioning.TryCreate(vault, out CredentialFailure failure) == null && failure == CredentialFailure.Denied,
            "a vault that refuses the key yields no identity");
    }

    private static BackupPolicy Settings(string root, MemoryVault vault)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "settings.txt"), "configuration before the change");
        File.WriteAllBytes(Path.Combine(source, "content.bin"), [0, 1, 255, 12]);
        JsonObject created = BackupKeyProvisioning.TryCreate(vault, out CredentialFailure failure)
            ?? throw new InvalidOperationException("Key provisioning failed: " + failure);
        var key = new SecretReference(Guid.Parse(created["keyId"]!.ToString()), created["keyVersion"]!.GetValue<int>());

        return new BackupPolicy
        {
            Source = source,
            Archives = Directory.CreateDirectory(Path.Combine(root, "archives")).FullName,
            Restores = Directory.CreateDirectory(Path.Combine(root, "restores")).FullName,
            KeyId = key.Id,
            KeyVersion = key.Version
        };
    }

    private static NodePolicy Policy(string root, string name, Scope[] scopes, BackupPolicy? backup)
    {
        string state = Directory.CreateDirectory(Path.Combine(root, name, "state")).FullName;
        string workspace = Directory.CreateDirectory(Path.Combine(root, name, "workspace")).FullName;

        return new NodePolicy
        {
            BackupRequirement = BackupRequirement.Off,
            NodeId = "backup-node",
            StateRoot = state,
            WorkspaceRoot = workspace,
            Backup = backup,
            Principals = [new PrincipalPolicy("operator", new string('a', 64), scopes, [], DateTimeOffset.UtcNow.AddHours(48))]
        };
    }

    private static JsonObject Creation(JsonObject plan, string? requestId = null)
    {
        return new JsonObject
        {
            ["backup_id"] = plan["backup_id"]!.ToString(),
            ["fingerprint"] = plan["fingerprint"]!.ToString(),
            ["request_id"] = requestId ?? Guid.CreateVersion7().ToString()
        };
    }

    private static JsonObject Restoration(JsonObject plan, string name)
    {
        return new JsonObject
        {
            ["backup_id"] = plan["backup_id"]!.ToString(),
            ["name"] = name,
            ["request_id"] = Guid.CreateVersion7().ToString()
        };
    }

    private static JsonObject Identified(Guid backupId)
    {
        return new JsonObject { ["backup_id"] = backupId.ToString() };
    }

    private static RequestContext Caller()
    {
        return new RequestContext("operator", Guid.CreateVersion7().ToString());
    }

    private static JsonObject Request(string tool, JsonObject? arguments = null)
    {
        return new JsonObject
        {
            ["action"] = tool,
            ["args"] = arguments ?? new JsonObject()
        };
    }

    // The clock of a node that runs the given time after the archives were written.
    private sealed class ShiftedClock(TimeSpan shift) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.UtcNow + shift;
        }
    }
}
