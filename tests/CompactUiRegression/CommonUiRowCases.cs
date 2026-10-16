using System.Text.Json.Nodes;
using KLaniBot.Platforms;
using KLaniBot.Transport.CompactUi;

// The common row writer is the source of the Windows compact tree. Its text must parse with the compact reader, keep
// the exact row grammar, match the parity step shared with the JS reference, and refuse a tree with an unknown field.
internal static class CommonUiRowCases
{
    private const string ParityStepName = "windows-common-rows";

    public static void Run()
    {
        WritesExactRows();
        ReaderAcceptsWrittenRows();
        RefusesUnknownFields();
        MatchesParityFixture();
    }

    private static void WritesExactRows()
    {
        Require(CommonUiRowFormat.TryFormat(SampleRows(), true, out string text), "rows with known fields format");
        // The JSON encoder writes a quote inside a value as a unicode escape, as the Linux and macOS rows do.
        string expected = string.Join("\n",
            "[1] Root \"Desktop 1\" (autoId: \"\", val: null, enabled: True, offscreen: False)",
            "  [2] Window \"Form\" (autoId: \"\", val: null, enabled: True, offscreen: False)",
            "    [3] Edit \"Name\" (autoId: \"name\", val: \"Size\\r\\nline \\u0022q\\u0022 \\\\ x\", enabled: False, offscreen: True)",
            "    [4] Button \"OK\" (autoId: \"\", val: null, enabled: True, offscreen: False)",
            "    [5] Edit \"Password\" (autoId: \"pwd\", val: null, enabled: True, offscreen: False)") + "\n";
        Require(text == expected, "exact common rows");
        Require(!text.Contains('\r'), "rows never contain a carriage return");
    }

    private static void ReaderAcceptsWrittenRows()
    {
        Require(CommonUiRowFormat.TryFormat(UnicodeRows(), true, out string text), "unicode rows format");
        Require(CompactTreeReader.TryParse(text, out var parsed), "compact reader accepts common rows");
        IReadOnlyList<CompactTreeNode> nodes = parsed.Value!;
        Require(nodes.Count == 3, "every row is kept");
        Require(nodes[0].Depth == 0 && nodes[0].Role.Family == CompactRoleFamily.DesktopRoot, "desktop root comes first");
        Require(nodes[1].Depth == 2 && nodes[1].Role.Family == CompactRoleFamily.Window, "nesting follows two spaces per level");
        Require(nodes[2].Value == "Größe \U0001F600" && nodes[2].Enabled && !nodes[2].Offscreen, "value keeps its text");
    }

    private static void RefusesUnknownFields()
    {
        Require(!CommonUiRowFormat.TryFormat(SampleRows(), false, out string incomplete) && incomplete == "", "failed walk is refused");
        foreach (CommonUiRowReading unknown in UnknownRows())
        {
            Require(!CommonUiRowFormat.TryFormat([unknown], true, out string text) && text == "", "unknown field is refused");
        }
        Require(CommonUiRowFormat.TryFormat([KnownRow(new(CommonValueState.Withheld, null))], true, out _),
            "withheld password is a deliberate null");
        Require(CommonUiRowFormat.TryFormat([KnownRow(new(CommonValueState.Absent, null))], true, out _),
            "absent value pattern is a deliberate null");
    }

    // The formatter output for these rows is the text of the shared parity step, which the JS reference parses too.
    private static void MatchesParityFixture()
    {
        Require(CommonUiRowFormat.TryFormat(ParityRows(), true, out string text), "parity rows format");
        string repository = SharedParityCases.FindRepository();
        string fixture = Path.Combine(repository, "tests/CompactUiRegression/fixtures/projection.json");
        JsonObject corpus = JsonNode.Parse(File.ReadAllText(fixture))!.AsObject();
        JsonObject? step = corpus["steps"]!.AsArray().Select(node => node!.AsObject())
            .FirstOrDefault(candidate => candidate["name"]?.GetValue<string>() == ParityStepName);
        Require(step != null, "parity fixture has the Windows step");
        Require(step?["tree"]?.GetValue<string>() == text, "formatter output equals the shared parity fixture");
    }

    private static IReadOnlyList<CommonUiRowReading> SampleRows()
    {
        return
        [
            new(0, 1, "Root", "Desktop 1", "", new(CommonValueState.Absent, null), true, false),
            new(1, 2, "Window", "Form", "", new(CommonValueState.Absent, null), true, false),
            new(2, 3, "Edit", "Name", "name", new(CommonValueState.Read, "Size\r\nline \"q\" \\ x"), false, true),
            new(2, 4, "Button", "OK", "", new(CommonValueState.Absent, null), true, false),
            new(2, 5, "Edit", "Password", "pwd", new(CommonValueState.Withheld, null), true, false)
        ];
    }

    private static IReadOnlyList<CommonUiRowReading> UnicodeRows()
    {
        return
        [
            new(0, 1, "Root", "Desktop 1", "", new(CommonValueState.Absent, null), true, false),
            new(1, 2, "Window", "Form", "", new(CommonValueState.Absent, null), true, false),
            new(2, 3, "Edit", "Größe", "", new(CommonValueState.Read, "Größe \U0001F600"), true, false)
        ];
    }

    private static IReadOnlyList<CommonUiRowReading> ParityRows()
    {
        return
        [
            new(0, 1, "Root", "Desktop 1", "", new(CommonValueState.Absent, null), true, false),
            new(1, 2, "Window", "Untitled - Notepad", "", new(CommonValueState.Absent, null), true, false),
            new(2, 3, "Edit", "Text editor", "Editor", new(CommonValueState.Read, "Größe\r\nhello \"you\""), true, false),
            new(2, 4, "Button", "Close", "Close", new(CommonValueState.Absent, null), true, false),
            new(2, 5, "Edit", "Find", "", new(CommonValueState.Read, ""), false, true),
            new(2, 6, "Edit", "Pass", "pwd", new(CommonValueState.Withheld, null), true, false)
        ];
    }

    private static IEnumerable<CommonUiRowReading> UnknownRows()
    {
        yield return KnownRow(new(CommonValueState.Absent, null)) with
        {
            Role = null
        };
        yield return KnownRow(new(CommonValueState.Absent, null)) with
        {
            Name = null
        };
        yield return KnownRow(new(CommonValueState.Absent, null)) with
        {
            AutoId = null
        };
        yield return KnownRow(new(CommonValueState.Absent, null)) with
        {
            Enabled = null
        };
        yield return KnownRow(new(CommonValueState.Absent, null)) with
        {
            Offscreen = null
        };
        yield return KnownRow(new(CommonValueState.Unreadable, null));
        yield return KnownRow(new(CommonValueState.Read, null));
    }

    private static CommonUiRowReading KnownRow(CommonValueReading value)
    {
        return new(1, 2, "Edit", "Name", "name", value, true, false);
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Common UI row regression failed: " + name);
        }
    }
}
