using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Shared capture fixtures contain explicit synthetic before/after trees, not outputs obtained or guessed from installed applications.
internal static class CaptureParityCases
{
    private static readonly Guid Evidence = Guid.Parse("11111111-1111-4111-8111-111111111111");

    public static JsonArray Run(string file)
    {
        JsonObject corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var replies = new JsonArray();
        foreach (JsonNode? fixture in corpus["cases"]!.AsArray())
        {
            JsonObject row = fixture!.AsObject();
            replies.Add(new JsonObject { ["name"] = row["name"]!.DeepClone(), ["reply"] = RunCase(row) });
        }

        return replies;
    }

    private static JsonObject RunCase(JsonObject row)
    {
        CompactTreeNode[] before = Nodes(row["before"]!.AsArray());
        CompactTreeNode[] after = Nodes(row["after"]!.AsArray());
        var references = row["references"]!.AsObject().ToDictionary(entry => entry.Key, entry => Target(entry.Value!.AsObject()));
        JsonObject args = row["arguments"]!.DeepClone().AsObject();
        if (args["evidence"]?.GetValue<string>() == "$evidence")
        {
            args["evidence"] = Evidence.ToString();
        }
        CompactCaptureAttempt<CompactFunctionDraft> compiled;
        if (row["kind"]?.GetValue<string>() == "read")
        {
            compiled = Read(args, references, after);
        }
        else
        {
            CompactDemonstrationStep[] steps = row["steps"]!.AsArray().Select(step => Step(step!.AsObject())).ToArray();
            CompactDemonstration? demonstration = row["no_demonstration"]?.GetValue<bool>() == true ? null
                : new(Evidence, 10, steps, before, after);
            if (!CompactCaptureCodec.TryRequest(args, demonstration, out var request))
            {
                return Error(request.Error!.Value);
            }
            CompactCaptureCompiler.TryCompile(demonstration!, request.Value!, references, out compiled);
        }

        return compiled.Value != null ? CompactCaptureWire.Draft(compiled.Value) : Error(compiled.Error!.Value);
    }

    private static CompactCaptureAttempt<CompactFunctionDraft> Read(JsonObject args,
        IReadOnlyDictionary<string, CompactSemanticTarget> references, IReadOnlyList<CompactTreeNode> after)
    {
        if (!CompactCaptureCodec.TryTopic(args["topic"]!.GetValue<string>(), out var topic)
            || !CompactCaptureCodec.TryField(args["field"]!.GetValue<string>(), out var field))
        {
            return new(null, CompactCaptureError.InvalidRead);
        }
        if (!references.TryGetValue(args["ref"]!.GetValue<string>(), out var target))
        {
            return new(null, CompactCaptureError.UnknownReference);
        }
        CompactCaptureCompiler.TryRead(args["name"]!.GetValue<string>(), topic!, args["output"]!.GetValue<string>(),
            args["pid"]!.GetValue<int>(), target, field, after, out var compiled);

        return compiled;
    }

    public static CompactTreeNode[] Nodes(JsonArray rows)
    {
        return rows.Select(row => new CompactTreeNode(row!["depth"]!.GetValue<int>(), row["id"]!.GetValue<int>(),
            CompactRole.Parse(row["role"]!.GetValue<string>()), row["name"]!.GetValue<string>(),
            row["autoId"]?.GetValue<string>() ?? "", row["value"]?.GetValue<string>(),
            row["enabled"]?.GetValue<bool>() ?? true, row["offscreen"]?.GetValue<bool>() ?? false)).ToArray();
    }

    public static CompactSemanticTarget Target(JsonObject row)
    {
        CompactContainerSelector[]? path = row["containerPath"]?.AsArray().Select(part => new CompactContainerSelector(
            CompactRole.Parse(part!["role"]!.GetValue<string>()), part["autoId"]?.GetValue<string>(),
            part["name"]?.GetValue<string>())).ToArray();

        return new(row["pid"]?.GetValue<int>() ?? 10, row["name"]!.GetValue<string>(), CompactRole.Parse(row["role"]!.GetValue<string>()),
            row["autoId"]?.GetValue<string>() ?? "",
            CompactWindowKindWords.TryParse(row["window"]?.GetValue<string>(), out var windowKind) ? windowKind : null, path);
    }

    private static CompactDemonstrationStep Step(JsonObject step)
    {
        CompactRecipeAction action = Enum.Parse<CompactRecipeAction>(step["action"]!.GetValue<string>(), true);
        string route = step["route"]!.GetValue<string>();
        CompactRecipeRoute typed = route switch
        {
            "native-text" => CompactRecipeRoute.NativeText,
            "native-choice" => CompactRecipeRoute.NativeChoice,
            "native-read" => CompactRecipeRoute.NativeRead,
            "semantic-focus+keyboard (native editable interface unavailable)" => CompactRecipeRoute.FocusKeyboard,
            _ => CompactRecipeRoute.Semantic
        };

        return new(action, step["target"] is JsonObject target ? Target(target) : null, typed, step["text"]?.GetValue<string>());
    }

    private static JsonObject Error(CompactCaptureError error)
    {
        return new()
        {
            ["error"] = CompactCaptureNames.Error(error)
        };
    }
}
