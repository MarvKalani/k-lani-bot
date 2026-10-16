using System.Diagnostics;
using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Use case: the regression run checks every window-state fixture case against its expectations, and the parity mode returns
// the built states so that the JS reference can be compared with the same fixture.
internal static class WindowStateCases
{
    private const string FixturePath = "tests/CompactUiRegression/fixtures/window-state.json";
    private const string FirstSecretCase = "password-dialog-a";
    private const string SecondSecretCase = "password-dialog-b";
    private const long RootOnlyHandle = 1;

    private const string RootOnlyTree =
        "[0] Root \"Accessibility desktop\" (autoId: \"\", val: null, enabled: False, offscreen: False)";

    // Parity mode returns one result per fixture case, named as the fixture names it.
    public static JsonArray Run(string file)
    {
        JsonArray results = new();
        foreach (JsonNode? entry in LoadCases(file))
        {
            JsonObject caseObject = entry!.AsObject();
            results.Add(new JsonObject
            {
                ["name"] = caseObject["name"]!.GetValue<string>(),
                ["result"] = BuildCase(caseObject)
            });
        }

        return results;
    }

    // Throws InvalidOperationException naming the first fixture case whose output differs from its expectation.
    public static void Check()
    {
        string file = Path.Combine(SharedParityCases.FindRepository(), FixturePath);
        Dictionary<string, string> shapes = new(StringComparer.Ordinal);
        foreach (JsonNode? entry in LoadCases(file))
        {
            JsonObject caseObject = entry!.AsObject();
            JsonObject output = BuildCase(caseObject);
            CheckCase(caseObject, output);
            CheckPart(caseObject, output);
            if (output["shape"] is JsonNode shape)
            {
                shapes[caseObject["name"]!.GetValue<string>()] = shape.GetValue<string>();
            }
        }

        RequireSameShape(shapes);
        RequireRootOnlyHasNoWindow();
    }

    private static void CheckCase(JsonObject entry, JsonObject output)
    {
        string name = entry["name"]!.GetValue<string>();
        if (entry.ContainsKey("expect"))
        {
            RequireSameJson(name, "expect", entry["expect"], output);
        }

        if (entry.ContainsKey("expect_lines"))
        {
            RequireLines(name, entry["expect_lines"]!.AsArray(), output["declaration"]!.GetValue<string>());
        }

        if (entry.ContainsKey("expect_state"))
        {
            RequireSameJson(name, "expect_state", entry["expect_state"], output["state"]);
        }

        if (entry.ContainsKey("expect_unmapped"))
        {
            RequireSameJson(name, "expect_unmapped", entry["expect_unmapped"], output["unmapped"]);
        }

        if (entry.ContainsKey("expect_hidden_omitted"))
        {
            RequireSameJson(name, "expect_hidden_omitted", entry["expect_hidden_omitted"], output["hidden_omitted"]);
        }

        if (entry.ContainsKey("expect_absent"))
        {
            RequireAbsent(name, entry["expect_absent"]!.GetValue<string>(), output);
        }
    }

    // Throws InvalidOperationException naming the case when its line count, cut map, shape or part differs.
    private static void CheckPart(JsonObject entry, JsonObject output)
    {
        string name = entry["name"]!.GetValue<string>();
        if (entry.ContainsKey("expect_line_count"))
        {
            RequireLineCount(name, entry["expect_line_count"]!.GetValue<int>(), output["declaration"]!.GetValue<string>());
        }

        if (entry.ContainsKey("expect_more"))
        {
            RequireSameJson(name, "expect_more", entry["expect_more"], output["more"]);
        }

        if (entry.ContainsKey("expect_shape"))
        {
            RequireSameJson(name, "expect_shape", entry["expect_shape"], output["shape"]);
        }

        if (entry.ContainsKey("expect_part"))
        {
            RequireSameJson(name, "expect_part", entry["expect_part"], output["part"]);
        }
    }

    // Throws InvalidOperationException when a fixture tree does not parse, because every case starts from a native tree.
    private static JsonObject BuildCase(JsonObject entry)
    {
        string name = entry["name"]!.GetValue<string>();
        JsonObject window = entry["window"]!.AsObject();
        long handle = window["handle"]!.GetValue<long>();
        CompactWindowKind kind = ParseKind(window["kind"]!.GetValue<string>());
        IReadOnlyList<CompactTreeNode> nodes = ParseTree(name, entry["tree"]!.GetValue<string>());
        CompactWindowStateRequest request = ReadRequest(handle, kind, entry);
        CompactWindowStateAttempt attempt = CompactWindowState.Build(nodes, request);
        return attempt.Value ?? ErrorObject(attempt.Error!.Value);
    }

    // Options hold max_chars and cursor; a case without options asks for the whole window with the default bounds.
    private static CompactWindowStateRequest ReadRequest(long handle, CompactWindowKind kind, JsonObject entry)
    {
        JsonObject options = entry["options"]?.AsObject() ?? new JsonObject();
        int maxChars = options["max_chars"]?.GetValue<int>() ?? CompactWindowStateLimits.DefaultMaxChars;
        string? cursor = options["cursor"]?.GetValue<string>();
        return new CompactWindowStateRequest(handle, kind, maxChars, cursor);
    }

    // A failed attempt is the whole case result, so the fixture expects the error word as its only field.
    private static JsonObject ErrorObject(CompactWindowStateError error)
    {
        return new JsonObject { ["error"] = ErrorWord(error) };
    }

    private static string ErrorWord(CompactWindowStateError error)
    {
        return error switch
        {
            CompactWindowStateError.NoWindow => "no_window",
            CompactWindowStateError.UnknownPart => "unknown_part",
            _ => throw new UnreachableException()
        };
    }

    // Throws InvalidOperationException when the fixture tree is not a native tree the reader accepts.
    private static IReadOnlyList<CompactTreeNode> ParseTree(string name, string tree)
    {
        if (!CompactTreeReader.TryParse(tree, out CompactTreeAttempt<IReadOnlyList<CompactTreeNode>> attempt) || attempt.Value is null)
        {
            throw new InvalidOperationException($"{name}: the fixture tree does not parse");
        }

        return attempt.Value;
    }

    // Throws InvalidOperationException for a kind word that the fixture does not define.
    private static CompactWindowKind ParseKind(string word)
    {
        return word switch
        {
            "main" => CompactWindowKind.Main,
            "dialog" => CompactWindowKind.Dialog,
            _ => throw new InvalidOperationException($"Unknown window kind {word}")
        };
    }

    private static JsonArray LoadCases(string file)
    {
        return JsonNode.Parse(File.ReadAllText(file))!.AsObject()["cases"]!.AsArray();
    }

    // Throws InvalidOperationException when the JSON of the expectation and of the built value differ.
    private static void RequireSameJson(string name, string label, JsonNode? expected, JsonNode? actual)
    {
        if (expected?.ToJsonString() != actual?.ToJsonString())
        {
            throw new InvalidOperationException($"{name}: {label} differs from the built state");
        }
    }

    // Throws InvalidOperationException when the declaration does not have the expected number of lines.
    private static void RequireLineCount(string name, int expected, string declaration)
    {
        int actual = declaration.Split('\n').Length;
        if (actual != expected)
        {
            throw new InvalidOperationException($"{name}: the declaration has {actual} lines, not {expected}");
        }
    }

    // Throws InvalidOperationException naming the first expected line that the declaration does not contain exactly.
    private static void RequireLines(string name, JsonArray expectedLines, string declaration)
    {
        string[] declaredLines = declaration.Split('\n');
        foreach (JsonNode? line in expectedLines)
        {
            string expectedLine = line!.GetValue<string>();
            if (!declaredLines.Contains(expectedLine))
            {
                throw new InvalidOperationException($"{name}: the declaration has no line {expectedLine}");
            }
        }
    }

    // Throws InvalidOperationException when the output contains text that must never appear, such as a secret value.
    private static void RequireAbsent(string name, string absentText, JsonObject output)
    {
        if (output.ToJsonString().Contains(absentText, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{name}: the state contains {absentText}");
        }
    }

    // Throws InvalidOperationException when the two password dialogs do not share one shape.
    private static void RequireSameShape(Dictionary<string, string> shapes)
    {
        if (shapes[FirstSecretCase] != shapes[SecondSecretCase])
        {
            throw new InvalidOperationException($"{FirstSecretCase} and {SecondSecretCase} differ in shape");
        }
    }

    // Throws InvalidOperationException when a tree with only a root row does not report the missing window.
    private static void RequireRootOnlyHasNoWindow()
    {
        IReadOnlyList<CompactTreeNode> nodes = ParseTree("root-only", RootOnlyTree);
        CompactWindowStateRequest request = new(RootOnlyHandle, CompactWindowKind.Main);
        CompactWindowStateAttempt attempt = CompactWindowState.Build(nodes, request);
        if (attempt.Error != CompactWindowStateError.NoWindow)
        {
            throw new InvalidOperationException("root-only: a tree without a window must report no_window");
        }
    }
}
