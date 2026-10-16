using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Guard;

// The backup section of policy.json must keep archives and restored plaintext away from the agent's workspace file tools,
// the node state and each other, and must name a vault key; a valid section loads and fixes its roots at start.
internal static class BackupPolicyCases
{
    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Backup policy");
        CheckValidSection(checks, temporaryRoot);
        CheckRefusedRoots(checks, temporaryRoot);
        CheckRefusedSettings(checks, temporaryRoot);

        return checks.Passed;
    }

    private static void CheckValidSection(Checks checks, string temporaryRoot)
    {
        var layout = new PolicyLayout(temporaryRoot);
        checks.That(Problem(layout, layout.Backup()) == null, "separate existing roots and a key load");
        checks.That(Problem(layout, null) == null, "a policy without backup section still loads");
        var loaded = NodePolicy.Load(layout.Write(layout.Backup()));
        checks.That(loaded.Backup!.MaximumAge == TimeSpan.FromHours(24), "receipts expire after 24 hours by default");
        File.WriteAllText(loaded.SourcePath, layout.PolicyText(layout.Backup(archives: layout.Make("other-archives"))));
        checks.That(Denied(() => loaded.Get("operator")), "moving the archive root needs a restart");
    }

    private static void CheckRefusedRoots(Checks checks, string temporaryRoot)
    {
        var layout = new PolicyLayout(temporaryRoot);
        string insideWorkspace = layout.Make(Path.Combine("workspace", "archives"));
        checks.That(Problem(layout, layout.Backup(archives: insideWorkspace))?.Contains("workspace") == true,
            "archives inside the workspace are refused");
        checks.That(Problem(layout, layout.Backup(restores: layout.Workspace)) != null, "the workspace itself cannot receive restores");
        string insideState = layout.Make(Path.Combine("state", "restores"));
        checks.That(Problem(layout, layout.Backup(restores: insideState)) != null, "restores inside the node state are refused");
        string configuration = Path.GetDirectoryName(layout.Write(null))!;
        checks.That(Problem(layout, layout.Backup(archives: configuration))?.Contains("policy file") == true,
            "archives cannot contain the policy file");
        string insideSource = layout.Make(Path.Combine("source", "archives"));
        checks.That(Problem(layout, layout.Backup(archives: insideSource)) != null, "archives cannot be inside the source");
        checks.That(Problem(layout, layout.Backup(restores: layout.Archives)) != null, "archives and restores must differ");
        checks.That(Problem(layout, layout.Backup(source: "source")) != null, "a relative source is refused");
        string missing = Path.Combine(layout.Root, "missing");
        checks.That(Problem(layout, layout.Backup(restores: missing)) != null, "a missing restore root is refused");
        string workspaceSource = layout.Make(Path.Combine("workspace", "data"));
        checks.That(Problem(layout, layout.Backup(source: workspaceSource)) == null, "the source may lie inside the workspace");
    }

    private static void CheckRefusedSettings(Checks checks, string temporaryRoot)
    {
        var layout = new PolicyLayout(temporaryRoot);
        checks.That(Problem(layout, layout.Backup(keyId: Guid.Empty)) != null, "an empty key id is refused");
        checks.That(Problem(layout, layout.Backup(keyVersion: 0)) != null, "a key version below one is refused");
        checks.That(Problem(layout, layout.Backup(maximumAgeHours: 0)) != null, "a zero maximum age is refused");
        checks.That(Problem(layout, layout.Backup(maximumAgeHours: 721)) != null, "a maximum age above 30 days is refused");
    }

    private static string? Problem(PolicyLayout layout, JsonObject? backup)
    {
        return NodePolicy.Read(layout.Write(backup)).Match<string?>(_ => null, problem => problem.Describe());
    }

    private static bool Denied(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    // One private directory tree per case group: workspace, state, source, archives and restores beside the policy file.
    private sealed class PolicyLayout
    {
        public PolicyLayout(string temporaryRoot)
        {
            Root = Path.Combine(temporaryRoot, "backup-policy-" + Guid.CreateVersion7().ToString("N"));
            Workspace = Make("workspace");
            Source = Make("source");
            Archives = Make("archives");
            Restores = Make("restores");
        }

        public string Root
        {
            get;
        }
        public string Workspace
        {
            get;
        }
        public string Source
        {
            get;
        }
        public string Archives
        {
            get;
        }
        public string Restores
        {
            get;
        }

        public string Make(string relative)
        {
            string directory = Path.Combine(Root, relative);
            Directory.CreateDirectory(directory);

            return directory;
        }

        public JsonObject Backup(string? source = null, string? archives = null, string? restores = null, Guid? keyId = null,
            int keyVersion = 1, int maximumAgeHours = 24)
        {
            return new JsonObject
            {
                ["source"] = source ?? Source,
                ["archives"] = archives ?? Archives,
                ["restores"] = restores ?? Restores,
                ["keyId"] = (keyId ?? Guid.CreateVersion7()).ToString(),
                ["keyVersion"] = keyVersion,
                ["maximumAgeHours"] = maximumAgeHours
            };
        }

        public string Write(JsonObject? backup)
        {
            string path = Path.Combine(Make("configuration"), "policy.json");
            File.WriteAllText(path, PolicyText(backup));

            return path;
        }

        public string PolicyText(JsonObject? backup)
        {
            var principal = new JsonObject
            {
                ["id"] = "operator",
                ["tokenSha256"] = new string('a', 64),
                ["scopes"] = new JsonArray("observe"),
                ["applications"] = new JsonArray(),
                ["expiresAt"] = "2100-01-01T00:00:00Z"
            };
            var policy = new JsonObject
            {
                ["nodeId"] = "backup-node",
                ["workspaceRoot"] = Workspace,
                ["stateRoot"] = Path.Combine(Root, "state"),
                ["principals"] = new JsonArray(principal)
            };
            if (backup != null)
            {
                policy["backup"] = backup;
            }

            return policy.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
    }
}
