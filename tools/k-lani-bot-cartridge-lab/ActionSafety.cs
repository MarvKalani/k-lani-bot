namespace KLaniBot.CartridgeLab;

// These facts are produced by this owned test double. Untrusted cartridge labels
// must not be accepted as facts at a production authorization boundary.
public sealed record ActionFacts(string Tool, string Context, string Effect, bool TargetBound = true,
    string[]? Keys = null, string Button = "left");
// Whether an operation is secure or probably harmful, the effect it has and why.
public sealed record RiskAssessment(string Category, string Effect, string Reason);
// A lab profile: which effects it grants and whether probably harmful operations may run at all.
public sealed record OperatorProfile(string Name, bool AllowProbablyHarmful, string[] Effects);
// The cartridge lab classifies each synthetic operation by its effect, before the fixture applies it, and lets the profile decide. A label
// from a cartridge is never taken as such a fact.
public static class ActionSafety
{
    public static RiskAssessment Assess(ActionFacts facts)
    {
        if (!facts.TargetBound)
            return new("probably_harmful", "unbound_input", "Target identity or focus is not bound.");
        if (facts.Tool == "hotkey")
            return AssessHotkey(facts);
        if (facts.Tool is "mouse_click" or "mouse_move" or "drag_and_drop")
            return new(
                "probably_harmful",
                facts.Button == "right" ? "context_menu" : "coordinate_input",
                "Coordinates and context menus do not prove the operation's effect.");
        if (facts.Context == "file_dialog" && facts.Effect == "application.edit")
            return new("probably_harmful", "unknown", "Generic editing in a file picker may rename or alter host files.");
        return facts.Effect switch
        {
            "read_ui" when facts.Tool is "find_elements" or "get_ui_tree" or "verify_element" =>
                new("secure", "read_ui", "Known observation; confidentiality is still governed by application/session authorization."),
            "application.edit" or "model.import" or "file.delete" or "file.rename" or "file.properties" or "application.delete"
                or "device.print" or "network.send"
                or "admin" => new("probably_harmful", facts.Effect, "Mutation or external effect requires a separate operator grant."),
            _ => new("probably_harmful", "unknown", "Unknown effect stays denied by default.")
        };
    }

    // A key chord says nothing about its effect without an application binding, so every chord is denied; the reason names
    // the most specific risk.
    private static RiskAssessment AssessHotkey(ActionFacts facts)
    {
        var keys = (facts.Keys ?? []).Select(k => k.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        if (keys.Count == 0)
            return new("probably_harmful", "unknown", "Unknown key chord.");
        if (keys.Overlaps(["win", "windows", "super", "meta", "cmd", "command"])
            || keys.Contains("alt") && keys.Overlaps(["tab", "f4", "escape"])
            || keys.Contains("ctrl") && keys.Contains("escape"))
            return new(
                "probably_harmful",
                "host_navigation",
                "Chord can escape the application; Command is conservatively restricted until an exact application binding exists.");
        if (keys.Contains("delete") || keys.Contains("backspace"))
            return new(
                "probably_harmful",
                facts.Context == "file_dialog" ? "file.delete" : "application.delete",
                "Deletion requires an explicit resource permission.");
        if (keys.Contains("f2") && facts.Context == "file_dialog")
            return new("probably_harmful", "file.rename", "A file picker is also a file management surface.");
        return new("probably_harmful", "unknown", "Hotkey meaning requires a reviewed application-specific binding.");
    }
    public static bool Allows(OperatorProfile profile, RiskAssessment assessment) =>
        assessment.Effect != "unknown" && profile.Effects.Contains(assessment.Effect, StringComparer.Ordinal) &&
        (assessment.Category == "secure" || profile.AllowProbablyHarmful);
    // Throws ArgumentException for an unknown profile name.
    public static OperatorProfile Profile(string name) => name switch
    {
        "observer" => new(name, false, ["read_ui"]),
        "model-editor" => new(name, true, ["read_ui", "model.import", "application.edit"]),
        "synthetic-file-manager" => new(name, true, ["read_ui", "file.delete", "file.rename", "file.properties"]),
        _ => throw new ArgumentException("Unknown local fixture profile.")
    };
}

// The state behind the lab's synthetic slicer window: a run applies operations here, and only these in-memory values change.
public sealed class FixtureModel(OperatorProfile profile, string? journal = null)
{
    public string ImportedModel { get; private set; } = "none";
    public string ProcessProfile { get; private set; } = "0.20mm Standard (synthetic)";
    public List<string> SyntheticFiles { get; } = ["synthetic-cube.stl", "synthetic-do-not-delete.txt"];
    public List<FixtureEvent> Events { get; } = [];
    public bool Apply(string operation, string value = "")
    {
        var facts = operation switch
        {
            "inspect" => new ActionFacts("verify_element", "application", "read_ui"),
            "import" => new ActionFacts("click_element", "file_dialog", "model.import"),
            "edit" => new ActionFacts("type_text", "application", "application.edit"),
            "delete-file" => new ActionFacts("click_element", "file_dialog", "file.delete"),
            "rename-file" => new ActionFacts("click_element", "file_dialog", "file.rename"),
            "properties" => new ActionFacts("click_element", "file_dialog", "file.properties"),
            "delete-master-data" => new ActionFacts("click_element", "application", "application.delete"),
            "print" => new ActionFacts("click_element", "application", "device.print"),
            _ => new ActionFacts("unknown", "unknown", "unknown")
        };
        var assessment = ActionSafety.Assess(facts);
        bool allowed = ActionSafety.Allows(profile, assessment);
        var entry = new FixtureEvent(Guid.CreateVersion7(), operation, assessment, allowed, "synthetic_only", value);
        Events.Add(entry);
        if (journal != null)
            File.AppendAllText(journal, System.Text.Json.JsonSerializer.Serialize(entry) + "\n");
        if (!allowed)
            return false;
        // Only owned in-memory strings are changed, never host files, printers or profiles.
        if (operation == "import")
            ImportedModel = value;
        if (operation == "edit")
            ProcessProfile = value;
        if (operation == "delete-file")
            SyntheticFiles.Remove("synthetic-do-not-delete.txt");
        if (operation == "rename-file")
        {
            SyntheticFiles.Remove("synthetic-cube.stl");
            SyntheticFiles.Add("synthetic-renamed.stl");
        }
        return true;
    }
}
// One operation the fixture was asked for, how it was assessed and whether the profile allowed it; written to the journal.
public sealed record FixtureEvent(Guid RequestId, string Operation, RiskAssessment Assessment, bool Allowed, string Boundary,
    string Value = "");
