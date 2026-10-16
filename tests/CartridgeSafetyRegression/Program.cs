using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions.Handlers;
using KLaniBot.CartridgeLab;

int checks = 0;
void Check(bool result, string name) { if (!result) throw new Exception(name); checks++; }
var observer = ActionSafety.Profile("observer");
var editor = ActionSafety.Profile("model-editor");
var read = ActionSafety.Assess(new("verify_element", "application", "read_ui"));
Check(read.Category == "secure" && ActionSafety.Allows(observer, read), "Known observation allowed");
foreach (string[] chord in new[]
{
    new[] { "win", "r" },
    ["CTRL", "WINDOWS", "x"],
    ["super", "e"],
    ["cmd", "space"],
    ["alt", "tab"],
    ["ctrl", "escape"]
})
{
    var assessment = ActionSafety.Assess(new("hotkey", "application", "read_ui", Keys: chord));
    Check(
        assessment.Effect == "host_navigation" && !ActionSafety.Allows(editor, assessment),
        "Global chord rejected despite read-only claim");
}
var filePickerEscapes = new Dictionary<string, string> { ["delete"] = "file.delete", ["f2"] = "file.rename" };
foreach ((string key, string expectedEffect) in filePickerEscapes)
{
    var assessment = ActionSafety.Assess(new("hotkey", "file_dialog", "application.edit", Keys: [key]));
    Check(assessment.Effect == expectedEffect && !ActionSafety.Allows(editor, assessment), "File picker escape denied");
}
Check(
    ActionSafety.Assess(new("mouse_click", "application", "read_ui", Button: "right")).Effect == "context_menu",
    "Right click is not harmless observation");
Check(
    !ActionSafety.Allows(editor, ActionSafety.Assess(new("mouse_click", "application", "application.edit"))),
    "Coordinate effects are unknown");
Check(
    !ActionSafety.Allows(editor, ActionSafety.Assess(new("click_element", "file_dialog", "application.edit"))),
    "File dialog is not a generic form");
Check(
    !ActionSafety.Allows(editor, ActionSafety.Assess(new("click_element", "application", "application.edit", false))),
    "Unbound target rejected");
Check(
    !ActionSafety.Allows(editor, ActionSafety.Assess(new("hotkey", "application", "read_ui", Keys: ["ctrl", "i"]))),
    "Unreviewed app hotkey rejected");
var model = new FixtureModel(editor);
Check(model.Apply("edit", "edited") && model.ProcessProfile == "edited", "Editor may edit");
Check(!model.Apply("delete-master-data"), "Editor may not delete master data");
Check(!model.Apply("delete-file") && model.SyntheticFiles.Count == 2, "Editor may not delete files in picker");
Check(!model.Apply("rename-file") && model.SyntheticFiles.Contains("synthetic-cube.stl"), "Rename denial leaves state unchanged");
Check(!model.Apply("properties") && !model.Apply("print"), "No properties or printer authority");
Check(model.Apply("import", "cube.stl") && model.ImportedModel == "cube.stl", "Explicit model import allowed");
Check(!new FixtureModel(observer).Apply("import", "cube.stl"), "Observation profile cannot import");
Check(
    !model.Apply("unknown")
        && !ActionSafety.Allows(new("admin", true, ["unknown"]), ActionSafety.Assess(new("unknown", "application", "unknown"))),
    "Unknown stays denied even under risk opt-in");
Check(model.Events.All(e => e.RequestId.Version == 7 && e.Boundary == "synthetic_only"), "Fixture journal uses UUIDv7 and names boundary");
var syntheticAdmin = new FixtureModel(ActionSafety.Profile("synthetic-file-manager"));
Check(
    syntheticAdmin.Apply("delete-file") && syntheticAdmin.SyntheticFiles.Count == 1 && !syntheticAdmin.Apply("print"),
    "File manager is not printer admin");
string root = Path.Combine(Path.GetTempPath(), "k-lani-bot-cartridge-safety-" + Guid.CreateVersion7());
Directory.CreateDirectory(root);
try
{
    var engine = new MacroEngine(Path.Combine(root, "macros.json"));
    var macro = new MacroDefinition
    {
        Name = "k_lani_bot_cardinality",
        Steps =
        [
            new JsonObject
            {
                ["action"] = "find_elements",
                ["args"] = new JsonObject { ["name"] = "Open" },
                ["capture"] = "target",
                ["expect_count"] = 1
            },
            new JsonObject { ["action"] = "click_element", ["args"] = new JsonObject { ["id"] = "{{target.matches.0.Id}}" } }
        ]
    };
    engine.SaveMacro(macro);
    int mutations = 0;
    JsonObject Dispatch(JsonObject value, int count, bool truncate = false)
    {
        if (value["action"]!.ToString() == "click_element")
        {
            mutations++;
            Check(Guid.Parse(value["args"]!["request_id"]!.ToString()).Version == 7, "Macro mutation UUIDv7");
            return new JsonObject { ["success"] = true };
        }
        return new JsonObject
        {
            ["success"] = true,
            ["total_matches"] = count,
            ["matches"] = new JsonArray(Enumerable
                .Range(1, truncate ? 1 : count)
                .Select(id => (JsonNode)new JsonObject { ["Id"] = id })
                .ToArray()),
            ["snapshot_id"] = "synthetic-snapshot"
        };
    }
    var result = JsonSerializer.SerializeToNode(engine.RunMacro(macro.Name, null, s => Dispatch(s, 2), macro.Sha256))!;
    Check(result["outcome"]!.ToString() == "selector_cardinality_failed" && mutations == 0, "Ambiguous selector fails before input");
    result = JsonSerializer.SerializeToNode(engine.RunMacro(macro.Name, null, s => Dispatch(s, 2, true), macro.Sha256))!;
    Check(result["success"]!.GetValue<bool>() == false && mutations == 0, "Truncated results cannot hide ambiguity");
    result = JsonSerializer.SerializeToNode(engine.RunMacro(macro.Name, null, s => Dispatch(s, 1), macro.Sha256))!;
    Check(result["success"]!.GetValue<bool>() && mutations == 1, "Unique selector proceeds");
    foreach (JsonNode invalid in new JsonNode[] { JsonValue.Create(-1)!, JsonValue.Create(201)!, JsonValue.Create("1")! })
    {
        var bad = new MacroDefinition
        {
            Name = "bad",
            Steps = [new JsonObject { ["action"] = "find_elements", ["args"] = new JsonObject(), ["expect_count"] = invalid }]
        };
        bool rejected = false;
        try
        {
            engine.SaveMacro(bad);
        }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "Invalid cardinality rejected");
    }
    var absence = new MacroDefinition
    {
        Name = "absence",
        Steps = [new JsonObject { ["action"] = "find_elements", ["args"] = new JsonObject(), ["expect_count"] = 0 }]
    };
    engine.SaveMacro(absence);
    result = JsonSerializer.SerializeToNode(engine.RunMacro(absence.Name, null, s => Dispatch(s, 0), absence.Sha256))!;
    Check(result["success"]!.GetValue<bool>(), "expect_count 0 accepts an empty result");
    result = JsonSerializer.SerializeToNode(engine.RunMacro(absence.Name, null, s => Dispatch(s, 1), absence.Sha256))!;
    Check(result["outcome"]!.ToString() == "selector_cardinality_failed", "expect_count 0 fails while a match remains");
    string bundle = Path.Combine(Directory.GetCurrentDirectory(), "cartridges/programs/orcaslicer/2.4.2");
    var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "cartridge.json")))!;
    foreach (var item in manifest["files"]!.AsArray())
    {
        string file = Path.Combine(bundle, item!["path"]!.ToString());
        byte[] bytes = File.ReadAllBytes(file);
        Check(
            bytes.Length == item["bytes"]!.GetValue<int>()
                && Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) == item["sha256"]!.ToString(),
            "Cartridge file declaration matches shipped bytes");
        if (item["path"]!.ToString().EndsWith(".json", StringComparison.Ordinal))
        {
            var recipe = JsonSerializer.Deserialize<MacroDefinition>(bytes)!;
            engine.SaveMacro(recipe);
            Check(recipe.Sha256 == MacroEngine.Hash(recipe), "Shipped macro imports as immutable version");
        }
    }
    Check(manifest["secured"]!.GetValue<bool>() == false
        && manifest["automatic_activation"]!.GetValue<bool>() == false, "Fixture or app replay cannot self-authorize community activation");
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"{checks} cartridge safety checks passed.");
