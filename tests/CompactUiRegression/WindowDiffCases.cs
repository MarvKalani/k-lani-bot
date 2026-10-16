using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Use case: the regression run checks the window diff of every fixture case; the parity mode returns the steps, the total and the
// bindings so that the JS reference can be compared with the same fixture.
internal static class WindowDiffCases
{
    private const string FixturePath = "tests/CompactUiRegression/fixtures/window-diff.json";

    // Parity mode returns one result per fixture case, named as the fixture names it.
    public static JsonArray Run(string file)
    {
        JsonArray results = new();
        foreach (JsonNode? entry in LoadCases(file))
        {
            results.Add(BuildCase(entry!.AsObject()));
        }

        return results;
    }

    // Throws InvalidOperationException naming the first fixture case whose steps, total or bindings differ from its expectation.
    public static void Check()
    {
        string file = Path.Combine(SharedParityCases.FindRepository(), FixturePath);
        foreach (JsonNode? entry in LoadCases(file))
        {
            JsonObject caseObject = entry!.AsObject();
            JsonObject output = BuildCase(caseObject);
            RequireSameJson(caseObject, "steps", "expect_steps", output);
            RequireSameJson(caseObject, "total", "expect_total", output);
            RequireSameJson(caseObject, "bindings", "expect_bindings", output);
        }
    }

    private static void RequireSameJson(JsonObject caseObject, string outputKey, string expectKey, JsonObject output)
    {
        string name = caseObject["name"]!.GetValue<string>();
        JsonNode? expected = caseObject[expectKey];
        JsonNode? actual = output[outputKey];
        if (expected?.ToJsonString() != actual?.ToJsonString())
        {
            throw new InvalidOperationException($"window diff case {name} differs in {outputKey}");
        }
    }

    // Each step diffs a tree with the next one, the total diffs the first tree with the last one, and the bindings name the rows
    // of the first tree that the fixture expects.
    private static JsonObject BuildCase(JsonObject caseObject)
    {
        string name = caseObject["name"]!.GetValue<string>();
        CompactWindowStateRequest request = ParseRequest(caseObject);
        List<IReadOnlyList<CompactTreeNode>> trees = ParseTrees(name, caseObject["trees"]!.AsArray());
        JsonArray steps = new();
        for (int index = 0; index + 1 < trees.Count; index++)
        {
            steps.Add(DiffOf(name, trees[index], trees[index + 1], request));
        }

        JsonObject output = new();
        output["name"] = JsonValue.Create(name);
        output["steps"] = steps;
        output["total"] = DiffOf(name, trees[0], trees[trees.Count - 1], request);
        output["bindings"] = BindingsOf(name, caseObject["expect_bindings"]?.AsObject(), trees[0]);
        return output;
    }

    private static CompactWindowStateRequest ParseRequest(JsonObject caseObject)
    {
        JsonObject window = caseObject["window"]!.AsObject();
        long handle = window["handle"]!.GetValue<long>();
        CompactWindowKind kind = ParseKind(window["kind"]!.GetValue<string>());
        int maxChars = caseObject["options"]?["max_chars"]?.GetValue<int>() ?? CompactWindowStateLimits.DefaultMaxChars;
        return new CompactWindowStateRequest(handle, kind, maxChars);
    }

    private static List<IReadOnlyList<CompactTreeNode>> ParseTrees(string name, JsonArray trees)
    {
        List<IReadOnlyList<CompactTreeNode>> parsed = new();
        foreach (JsonNode? tree in trees)
        {
            parsed.Add(ParseTree(name, tree!.GetValue<string>()));
        }

        return parsed;
    }

    private static JsonObject DiffOf(
        string name,
        IReadOnlyList<CompactTreeNode> before,
        IReadOnlyList<CompactTreeNode> after,
        CompactWindowStateRequest request)
    {
        CompactWindowDiffAttempt attempt = CompactWindowDiff.Between(before, after, request);
        if (attempt.Value is null)
        {
            throw new InvalidOperationException($"window diff case {name} has a tree without a window");
        }

        return attempt.Value;
    }

    // The bindings come from the first tree, because the fixture expects the bindings of the window before its steps.
    private static JsonObject BindingsOf(string name, JsonObject? expectBindings, IReadOnlyList<CompactTreeNode> firstTree)
    {
        JsonObject bindings = new();
        if (expectBindings is null)
        {
            return bindings;
        }

        CompactWindowBindingsAttempt attempt = CompactWindowState.Bind(firstTree);
        if (attempt.Value is null)
        {
            throw new InvalidOperationException($"window diff case {name} has a tree without a window");
        }

        foreach (KeyValuePair<string, JsonNode?> pair in expectBindings)
        {
            bindings[pair.Key] = BindingNode(attempt.Value, pair.Key);
        }

        return bindings;
    }

    // A path without a binding reads as null, so a missing row shows up as a difference instead of a silent skip.
    private static JsonNode? BindingNode(IReadOnlyDictionary<string, CompactMemberBinding> bindings, string path)
    {
        if (!bindings.TryGetValue(path, out CompactMemberBinding? binding))
        {
            return null;
        }

        JsonObject node = new();
        node["id"] = JsonValue.Create(binding.NodeId);
        node["member"] = JsonValue.Create(CompactWindowState.WireName(binding.Member));
        node["enabled"] = JsonValue.Create(binding.Enabled);
        node["fallback"] = JsonValue.Create(binding.Fallback);
        return node;
    }

    private static JsonArray LoadCases(string file)
    {
        JsonObject document = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        return document["cases"]!.AsArray();
    }

    private static IReadOnlyList<CompactTreeNode> ParseTree(string name, string tree)
    {
        if (!CompactTreeReader.TryParse(tree, out CompactTreeAttempt<IReadOnlyList<CompactTreeNode>> attempt) || attempt.Value is null)
        {
            throw new InvalidOperationException($"window diff case {name} has a tree that does not parse");
        }

        return attempt.Value;
    }

    private static CompactWindowKind ParseKind(string word)
    {
        return word switch
        {
            "main" => CompactWindowKind.Main,
            "dialog" => CompactWindowKind.Dialog,
            _ => throw new InvalidOperationException($"unknown window kind {word}")
        };
    }
}
