using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Common;
using KLaniBot.Guard;

// Scopes are words in two files the node does not write: the embedded tool contract and the operator's policy. Every
// word in the contract must be a scope, and a policy that grants something that is no scope must not load.
internal static class ScopeCases
{
    public static int Run(string temporaryRoot)
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Scope: " + name);
            }
            passed++;
        }

        foreach (JsonObject tool in ToolContract.Tools.OfType<JsonObject>())
        {
            Check(WireNames<Scope>.TryParse(tool["scope"]?.ToString(), out _), "tool " + tool["name"] + " names a known scope");
        }
        foreach (Scope scope in Enum.GetValues<Scope>())
        {
            Check(WireNames<Scope>.TryParse(WireNames<Scope>.Of(scope), out Scope parsed) && parsed == scope, "round trip of " + scope);
        }
        Check(JsonSerializer.Serialize(new[] { Scope.FilesRead }) == "[\"files.read\"]", "a scope is written as its word");
        Check(PolicyIsRefused(temporaryRoot, "[\"*\"]"), "a wildcard is no scope");
        Check(PolicyIsRefused(temporaryRoot, "[\"observe\", \"teleport\"]"), "an unknown word is no scope");
        Check(PolicyIsRefused(temporaryRoot, "[0]"), "a number is no scope");
        Check(!PolicyIsRefused(temporaryRoot, "[\"observe\", \"files.read\"]"), "known words load");
        return passed;
    }

    private static bool PolicyIsRefused(string temporaryRoot, string scopesJson)
    {
        string root = Path.Combine(temporaryRoot, "scope-policy-" + Guid.NewGuid().ToString("N"));
        string workspace = Path.Combine(root, "workspace");
        string state = Path.Combine(root, "state");
        Directory.CreateDirectory(workspace);
        string policyPath = Path.Combine(root, "policy.json");
        string principal = "{\"id\":\"operator\",\"tokenSha256\":\"" + new string('a', 64) + "\",\"scopes\":" + scopesJson
            + ",\"applications\":[],\"expiresAt\":\"2100-01-01T00:00:00Z\"}";
        string policy = "{\"nodeId\":\"scope-node\",\"workspaceRoot\":" + JsonSerializer.Serialize(workspace)
            + ",\"stateRoot\":" + JsonSerializer.Serialize(state) + ",\"principals\":[" + principal + "]}";
        File.WriteAllText(policyPath, policy);

        try
        {
            NodePolicy.Load(policyPath);
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }
}
