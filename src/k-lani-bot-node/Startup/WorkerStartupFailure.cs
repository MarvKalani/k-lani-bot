using System.Text.Json.Nodes;
using KLaniBot.Common;
using KLaniBot.Guard;
using KLaniBot.Cartridges;

namespace KLaniBot.Startup;

// A failed visible worker may have no console. Preserve a bounded, cleaned startup cause beside its private policy
// so rollback does not discard the evidence needed to repair the next update.
internal static class WorkerStartupFailure
{
    public static void Record(string[] arguments, Exception failure)
    {
        try
        {
            string? policyPath = new NodeArguments(arguments).Option("--policy");
            if (policyPath == null)
            {
                return;
            }
            NodePolicy policy = NodePolicy.Load(policyPath);
            Exception cause = failure;
            while (cause.InnerException != null)
            {
                cause = cause.InnerException;
            }
            var report = new JsonObject
            {
                ["version"] = ProductIdentity.DisplayVersion,
                ["type"] = failure.GetType().Name,
                ["cause_type"] = cause.GetType().Name,
                ["hresult"] = "0x" + cause.HResult.ToString("X8"),
                ["diagnostic"] = Diagnostics.Describe(cause, policy.StateRoot),
                ["stack"] = Diagnostics.Clean(cause.StackTrace ?? "", policy.StateRoot),
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("O")
            };
            string path = Path.Combine(policy.StateRoot, "worker-startup-failure.json");
            CartridgeManifest.RejectLinks(path);
            PrivateFile.WriteAllText(path, report.ToJsonString(), replace: true);
        }
        catch (Exception)
        {
            // Startup failure reporting must not replace the original cause or prevent the supervisor's rollback.
        }
    }
}
