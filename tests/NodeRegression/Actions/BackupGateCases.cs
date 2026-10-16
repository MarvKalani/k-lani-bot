using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Backup;
using KLaniBot.Credentials;
using KLaniBot.Guard;

// The first change of a session needs a current backup unless the administrator switched the requirement off: without a
// backup section or archive it is refused before it starts, a covered session keeps working while the source changes, a
// new session after a change needs a new backup, and the switch is read live from the policy file.
internal static class BackupGateCases
{
    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Backup gate");
        string root = Path.Combine(temporaryRoot, "backup-gate-" + Guid.CreateVersion7().ToString("N"));
        using var vault = new MemoryVault();
        CheckWithoutBackup(checks, root, vault);
        CheckCoveredSession(checks, root, vault);
        CheckLiveSwitch(checks, root, vault);

        return checks.Passed;
    }

    private static void CheckWithoutBackup(Checks checks, string root, MemoryVault vault)
    {
        NodePolicy unconfigured = Policy(root, "unconfigured", null, BackupRequirement.Required);
        using (var dispatcher = new ActionDispatcher(new FakeUi(), unconfigured, backupKeys: vault))
        {
            JsonObject refused = dispatcher.Execute(Write("a.txt"), Caller());
            checks.That(refused["code"]?.ToString() == "backup_required" && refused["outcome"]?.ToString() == "not_dispatched",
                "a change without backup section is refused before it starts");
            checks.That(refused["error"]?.ToString().Contains("switch the requirement off") == true, "the answer names both ways out");
            checks.That(!File.Exists(Path.Combine(unconfigured.WorkspaceRoot, "a.txt")), "nothing was written");
        }
        NodePolicy configured = Policy(root, "no-archive", Settings(root, "no-archive", vault), BackupRequirement.Required);
        using (var dispatcher = new ActionDispatcher(new FakeUi(), configured, backupKeys: vault))
        {
            JsonObject refused = dispatcher.Execute(Write("a.txt"), Caller());
            checks.That(refused["error"]?.ToString().Contains("backup_create") == true, "without an archive the answer asks for one");
        }
        NodePolicy off = Policy(root, "off", null, BackupRequirement.Off);
        using (var dispatcher = new ActionDispatcher(new FakeUi(), off, backupKeys: vault))
        {
            checks.That(dispatcher.Execute(Write("a.txt"), Caller())["success"]?.GetValue<bool>() == true,
                "with the requirement switched off the change runs");
        }
    }

    private static void CheckCoveredSession(Checks checks, string root, MemoryVault vault)
    {
        BackupPolicy settings = Settings(root, "covered", vault);
        NodePolicy policy = Policy(root, "covered", settings, BackupRequirement.Required);
        using var dispatcher = new ActionDispatcher(new FakeUi(), policy, backupKeys: vault);
        RequestContext first = Caller();
        JsonObject plan = dispatcher.Execute(Request("backup_plan", new JsonObject()), first);
        JsonObject created = dispatcher.Execute(Request("backup_create", Creation(plan)), first);
        checks.That(created["success"]?.GetValue<bool>() == true, "creating the backup itself needs no backup");

        JsonObject step = Write("one.txt");
        checks.That(dispatcher.Execute(step, first)["success"]?.GetValue<bool>() == true, "a current backup admits the change");
        File.WriteAllText(Path.Combine(settings.Source, "settings.txt"), "changed by the first step");
        checks.That(dispatcher.Execute(Write("two.txt"), first)["success"]?.GetValue<bool>() == true,
            "later steps of the covered session continue although the source changed");
        JsonObject other = dispatcher.Execute(Write("three.txt"), Caller());
        checks.That(other["code"]?.ToString() == "backup_required", "a new session after a change needs a new backup");
        JsonObject replay = dispatcher.Execute((JsonObject)step.DeepClone(), first);
        checks.That(replay["replayed"]?.GetValue<bool>() == true, "a retried request still gets its recorded answer");

        var later = new ShiftedClock(TimeSpan.FromHours(25));
        using var laterDispatcher = new ActionDispatcher(new FakeUi(), policy, clock: later, backupKeys: vault);
        JsonObject aged = laterDispatcher.Execute(Write("four.txt"), Caller());
        checks.That(aged["code"]?.ToString() == "backup_required", "an archive older than the maximum age covers no new session");
    }

    private static void CheckLiveSwitch(Checks checks, string root, MemoryVault vault)
    {
        NodePolicy written = Policy(root, "live", null, BackupRequirement.Off);
        string file = Path.Combine(root, "live", "policy.json");
        File.WriteAllText(file, PolicyText(written));
        NodePolicy loaded = NodePolicy.Load(file);
        using var dispatcher = new ActionDispatcher(new FakeUi(), loaded, backupKeys: vault);
        checks.That(dispatcher.Execute(Write("a.txt"), Caller())["success"]?.GetValue<bool>() == true, "off in the file admits");

        written.BackupRequirement = BackupRequirement.Required;
        File.WriteAllText(file, PolicyText(written));
        JsonObject refused = dispatcher.Execute(Write("b.txt"), Caller());
        checks.That(refused["code"]?.ToString() == "backup_required", "switching it on in the file applies without a restart");
    }

    private static string PolicyText(NodePolicy policy)
    {
        var principal = new JsonObject
        {
            ["id"] = "operator",
            ["tokenSha256"] = new string('a', 64),
            ["scopes"] = new JsonArray("observe", "files.write"),
            ["applications"] = new JsonArray(),
            ["expiresAt"] = "2100-01-01T00:00:00Z"
        };
        var document = new JsonObject
        {
            ["nodeId"] = policy.NodeId,
            ["workspaceRoot"] = policy.WorkspaceRoot,
            ["stateRoot"] = policy.StateRoot,
            ["backupRequirement"] = JsonSerializer.SerializeToNode(policy.BackupRequirement),
            ["principals"] = new JsonArray(principal)
        };
        return document.ToJsonString();
    }

    private static BackupPolicy Settings(string root, string name, MemoryVault vault)
    {
        string source = Directory.CreateDirectory(Path.Combine(root, name, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "settings.txt"), "configuration before the change");
        JsonObject created = BackupKeyProvisioning.TryCreate(vault, out CredentialFailure failure)
            ?? throw new InvalidOperationException("Key provisioning failed: " + failure);

        return new BackupPolicy
        {
            Source = source,
            Archives = Directory.CreateDirectory(Path.Combine(root, name, "archives")).FullName,
            Restores = Directory.CreateDirectory(Path.Combine(root, name, "restores")).FullName,
            KeyId = Guid.Parse(created["keyId"]!.ToString()),
            KeyVersion = created["keyVersion"]!.GetValue<int>()
        };
    }

    private static NodePolicy Policy(string root, string name, BackupPolicy? backup, BackupRequirement requirement)
    {
        Scope[] scopes = [Scope.Observe, Scope.FilesWrite, Scope.BackupRead, Scope.BackupCreate];

        return new NodePolicy
        {
            NodeId = "gate-node",
            StateRoot = Directory.CreateDirectory(Path.Combine(root, name, "state")).FullName,
            WorkspaceRoot = Directory.CreateDirectory(Path.Combine(root, name, "workspace")).FullName,
            Backup = backup,
            BackupRequirement = requirement,
            Principals = [new PrincipalPolicy("operator", new string('a', 64), scopes, [], DateTimeOffset.UtcNow.AddHours(48))]
        };
    }

    private static JsonObject Write(string path)
    {
        var arguments = new JsonObject
        {
            ["path"] = path,
            ["content"] = "changed",
            ["request_id"] = Guid.CreateVersion7().ToString()
        };
        return Request("fs_write_file", arguments);
    }

    private static JsonObject Creation(JsonObject plan)
    {
        var arguments = new JsonObject
        {
            ["backup_id"] = plan["backup_id"]!.ToString(),
            ["fingerprint"] = plan["fingerprint"]!.ToString(),
            ["request_id"] = Guid.CreateVersion7().ToString()
        };
        return arguments;
    }

    private static JsonObject Request(string tool, JsonObject arguments)
    {
        return new JsonObject { ["action"] = tool, ["args"] = arguments };
    }

    private static RequestContext Caller()
    {
        return new RequestContext("operator", Guid.CreateVersion7().ToString());
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
