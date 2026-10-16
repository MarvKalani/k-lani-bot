using System.Text.Json;
using KLaniBot.Common;
using KLaniBot.Guard;
using KLaniBot.Startup;

// node.json and policy.json are files an operator may edit by hand. A damaged one must give a problem that names the
// field, never a null reference in the middle of the start-up.
internal static class NodeConfigurationCases
{
    public static int Run(string temporaryRoot)
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Node configuration: " + name);
            }
            passed++;
        }

        string directory = Path.Combine(temporaryRoot, "node-configuration");
        Directory.CreateDirectory(directory);
        var written = new NodeConfiguration("/state/policy.json", ["worker", "--stdio-worker"], "127.0.0.1", 5443, 60000);
        Check(KindOf(directory, written.ToJson().ToJsonString()) == null, "what --init writes is read back");
        Check(ReadBack(directory, written.ToJson().ToJsonString()) is { HttpsPort: 5443 } back
            && back.WorkerCommand.SequenceEqual(written.WorkerCommand), "the values survive the round trip");
        Check(KindOf(directory, "{\"policy\": \"/p\", \"worker\": [\"w\"]}") == null, "bind, port and deadline have defaults");
        Check(KindOf(directory, "{\"policy\": \"/p\", \"wor") == ReadProblemKind.NotValid, "a truncated file is no JSON");
        Check(KindOf(directory, "[]") == ReadProblemKind.NotValid, "an array is no configuration");
        Check(KindOf(directory, "{\"worker\": [\"w\"]}") == ReadProblemKind.Missing, "the policy path is required");
        Check(KindOf(directory, "{\"policy\": \"/p\", \"worker\": [1]}") == ReadProblemKind.Missing, "a worker word must be text");
        Check(KindOf(directory, "{\"policy\": \"/p\", \"worker\": [\"w\"], \"httpsPort\": 70000}") == ReadProblemKind.Refused,
            "a port outside 1 to 65535 is refused");
        Check(KindOf(directory, "{\"policy\": \"/p\", \"worker\": [\"w\"], \"httpsPort\": \"443\"}") == ReadProblemKind.Refused,
            "a port given as text is refused, not replaced by the default");
        Check(KindOf(directory, "{\"policy\": \"/p\", \"worker\": [\"w\"], \"bind\": \"localhost\"}") == ReadProblemKind.Refused,
            "bind must be an IP address");
        bool missingFileIsProblem = NodeConfiguration.Read(Path.Combine(directory, "absent.json"))
            .Match(_ => false, problem => problem.Kind == ReadProblemKind.Unreadable);
        Check(missingFileIsProblem, "a missing file is a problem, not an exception");

        string policyPath = Path.Combine(directory, "policy.json");
        File.WriteAllText(policyPath, "{\"workspaceRoot\": " + JsonSerializer.Serialize(Path.Combine(directory, "absent-workspace"))
            + ", \"stateRoot\": " + JsonSerializer.Serialize(Path.Combine(directory, "state")) + "}");
        bool refused = NodePolicy.Read(policyPath).Match(_ => false, problem => problem.Kind == ReadProblemKind.Refused);
        Check(refused, "a policy whose workspace does not exist is refused");
        Check(!Directory.Exists(Path.Combine(directory, "state")), "reading a refused policy creates nothing on disk");
        return passed;
    }

    private static ReadProblemKind? KindOf(string directory, string content)
    {
        string path = Path.Combine(directory, "node.json");
        File.WriteAllText(path, content);
        return NodeConfiguration.Read(path).Match(_ => (ReadProblemKind?)null, problem => problem.Kind);
    }

    private static NodeConfiguration? ReadBack(string directory, string content)
    {
        string path = Path.Combine(directory, "node.json");
        File.WriteAllText(path, content);
        return NodeConfiguration.Read(path).Match(configuration => configuration, _ => (NodeConfiguration?)null);
    }
}
