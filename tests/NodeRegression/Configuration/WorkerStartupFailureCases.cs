using System.Text.Json.Nodes;
using System.Text.Json;
using KLaniBot.Startup;
using KLaniBot.Guard;

// A failed desktop bootstrap must retain its deepest cleaned cause even when Windows started it without a console.
internal static class WorkerStartupFailureCases
{
    public static int Run(string root)
    {
        var checks = new Checks("Worker startup failure");
        string directory = Path.Combine(root, "startup-failure");
        Directory.CreateDirectory(directory);
        string workspace = Path.Combine(root, "startup-workspace");
        Directory.CreateDirectory(workspace);
        string policy = Path.Combine(directory, "policy.json");
        var settings = new NodePolicy
        {
            BackupRequirement = BackupRequirement.Off,
            StateRoot = directory,
            WorkspaceRoot = workspace,
            Principals = [new PrincipalPolicy("fixture", new string('a', 64), [Scope.Observe], [], DateTimeOffset.UtcNow.AddHours(1))
            {
                UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop }
            }]
        };
        File.WriteAllText(policy, JsonSerializer.Serialize(settings));
        var cause = new InvalidOperationException("Nested resource initialization failed.");
        WorkerStartupFailure.Record(["--policy", policy], new Exception("Outer startup failed.", cause));
        string path = Path.Combine(directory, "worker-startup-failure.json");
        checks.That(File.Exists(path), "startup failures are retained when no console is visible");
        JsonObject result = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        checks.That(result["version"]!.ToString() == ProductIdentity.DisplayVersion
            && result["cause_type"]!.ToString() == "InvalidOperationException",
            "the exact product version and deepest cause survive a later rollback");
        checks.That(result["diagnostic"]!.ToString().Contains("resource initialization", StringComparison.Ordinal),
            "the retained diagnostic names the concrete failure");

        return checks.Passed;
    }
}
