using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// The compact projection regression verifies native grammar, current target/type scope and reference publication without OS input.
internal static class TreeCases
{
    public static void Run()
    {
        ParseAndResolveTargets();
        ContainersAndStableReferences();
        DocumentAndModalScope();
        PagingAndHiddenReferences();
        NestedSameTitleWindowRows();
        StoredCaptionKeyIsInvalid();
        CaptionFreeIdentity();
    }

    private static void ParseAndResolveTargets()
    {
        var source = Nodes(Row(1, "Window", "Inbox") + "\n" + Row(2, "button", "Delete", depth: 2, autoId: "delete"));
        CompactSemanticTarget target = CompactSemanticIdentity.Target(10, source, source[1], null);
        Require(CompactTargetResolver.TryResolve(source, target, false, out var resolved) && resolved.Value == 2, "exact target");
        var renamed = Nodes(Row(3, "Window", "Inbox") + "\n" + Row(4, "button", "Löschen", depth: 2, autoId: "delete"));
        Require(CompactTargetResolver.TryResolve(renamed, target, true, out var stable) && stable.Value == 4, "stable ID caption");
        var wrong = Nodes(Row(3, "Window", "Inbox") + "\n" + Row(4, "Edit", "Delete", depth: 2, autoId: "delete"));
        Require(!CompactTargetResolver.TryResolve(wrong, target, true, out var type)
            && type.Error == CompactTreeError.TargetTypeChanged, "fresh role/type");
        var foreign = Nodes(Row(3, "Window", "Inbox") + "\n" + Row(4, "button", "Delete", depth: 2, autoId: "delete") + "\n"
            + Row(5, "Window", "Inbox archive"));
        Require(!CompactTargetResolver.TryResolve(foreign, target, true, out var changed)
            && changed.Error == CompactTreeError.WindowIdentityChanged, "several owning windows are refused");
        Require(!CompactTreeReader.TryParse("Unknown format", out var invalid)
            && invalid.Error == CompactTreeError.UnsupportedFormat, "unsupported tree");
    }

    private static void ContainersAndStableReferences()
    {
        var projection = new CompactUiProjection();
        var source = Nodes(Row(1, "Root", "Desktop") + "\n" + Row(2, "Window", "Form", depth: 2) + "\n"
            + Row(3, "panel", "Fields", depth: 4) + "\n" + Row(4, "Edit", "Old caption", depth: 6, autoId: "field"));
        var options = new CompactProjectionOptions("node", "verified-lifetime", ObservedWindow: Observed("Form", 100, "start"));
        Require(projection.TryObserve(10, source, options, out var first), "observe");
        string reference = first.Value!.Elements.Single(row => row.Attributes["role"]!.GetValue<string>() == "Edit").Reference;
        Require(projection.TryTarget(reference, 10, out var target), "observed reference");
        Require(target.Value!.ContainerPath!.Count == 1
            && target.Value.ContainerPath[0].Name == "Fields", "window-relative path");
        var renamed = Nodes(Row(20, "Window", "New title") + "\n" + Row(30, "panel", "Fields", depth: 2)
            + "\n" + Row(40, "Edit", "New caption", depth: 4, autoId: "field"));
        var changedOptions = options with
        {
            ObservedWindow = Observed("New title", 100, "start")
        };
        Require(projection.TryObserve(10, renamed, changedOptions, out var changed), "renamed instance");
        Require(changed.Value!.Changed.Any(row => row.Reference == reference), "stable reference");
        Require(projection.TryTarget(reference, 10, out var current) && current.Value!.WindowKind == CompactWindowKind.Main, "fresh kind");
        Require(CompactTargetResolver.TryResolve(renamed, current.Value!, true, out var resolved)
            && resolved.Value == 40, "fresh native ID");
        Require(!projection.TryTarget(reference, 20, out _), "foreign PID");
        projection.ResetApplication(10);
        Require(!projection.TryTarget(reference, 10, out _), "replacement reset");
    }

    private static void DocumentAndModalScope()
    {
        var projection = new CompactUiProjection();
        var source = Nodes(Row(1, "Window", "Browser") + "\n" + Row(2, "button", "Browser toolbar", depth: 2) + "\n"
            + Row(3, "document web", "Application", "\uFFFC", depth: 2) + "\n" + Row(4, "button", "Addieren", "Addieren", depth: 4)
            + "\n" + Row(5, "static", "Addieren", "Addieren", depth: 6) + "\n" + Row(6, "Edit", "Beträge", offscreen: true, depth: 4));
        Require(projection.TryObserve(10, source, new("node", "instance"), out var document), "document root");
        Require(!document.Value!.Elements.Any(row => row.Attributes["name"]!.GetValue<string>() == "Browser toolbar"), "drop chrome");
        Require(document.Value.Elements.Count(row => row.Attributes["name"]!.GetValue<string>() == "Addieren") == 1,
            "drop duplicate label");
        Require(document.Value.Elements.Any(row => row.Attributes["offscreen"] != null), "keep offscreen controls");
        var modal = Nodes(Row(1, "Window", "Browser") + "\n" + Row(2, "document web", "Application", depth: 2) + "\n"
            + Row(3, "button", "Underlying", depth: 4) + "\n" + Row(4, "dialog", "Confirm") + "\n" + Row(5, "button", "Cancel", depth: 2));
        Require(projection.TryObserve(10, modal, new("node", "instance"), out var dialog), "modal scope");
        Require(!dialog.Value!.Changed.Any(row => row.Attributes["name"]!.GetValue<string>() == "Underlying"), "modal excludes underlying");
        var roots = Nodes(Row(1, "document web", "First") + "\n" + Row(2, "document web", "Second"));
        Require(!projection.TryObserve(10, roots, new("node", "instance"), out var ambiguous)
            && ambiguous.TreeError == CompactTreeError.RootAmbiguousOrAbsent, "multiple document roots");
    }

    private static void PagingAndHiddenReferences()
    {
        var projection = new CompactUiProjection();
        var nodes = Nodes(string.Join('\n', Enumerable.Range(1, 250).Select(id => Row(id, "Edit", "Field " + id))));
        var options = new CompactProjectionOptions("node", "instance", Page: new(Limit: 1),
            ObservedWindow: Observed("Paging", 100, "start"));
        Require(projection.TryObserve(10, nodes, options, out var first), "bounded first page");
        string shown = first.Value!.Elements.Single().Reference;
        Require(projection.TryTarget(shown, 10, out _), "published reference");
        Require(!projection.TryTarget("e2", 10, out _), "unpublished reference denied");
        projection.HideUnseen(new HashSet<string>(), 10);
        Require(!projection.TryTarget(shown, 10, out _), "hidden response invalidates unseen reference");
        Require(projection.TryObserve(10, nodes, options, out var reset)
            && reset.Value!.BaselineReason == CompactBaselineReason.Reset, "next observation reset");
    }

    private static IReadOnlyList<CompactTreeNode> Nodes(string tree)
    {
        Require(CompactTreeReader.TryParse(tree, out var parsed), "native grammar");

        return parsed.Value!;
    }

    // A UWP frame and its inner CoreWindow share one caption; the outermost row owns both, so the inner control stays unique.
    private static void NestedSameTitleWindowRows()
    {
        var source = Nodes(Row(1, "Window", "Rechner") + "\n" + Row(2, "Window", "Rechner", depth: 2) + "\n"
            + Row(3, "button", "Eins", depth: 4, autoId: "one"));
        var target = new CompactSemanticTarget(10, "Eins", CompactRole.Parse("button"), "one", CompactWindowKind.Main, null);
        Require(CompactTargetResolver.TryResolve(source, target, true, out var resolved) && resolved.Value == 3,
            "nested same-title window rows resolve the inner control");
    }

    // A stored selector names its window by kind; a caption key is an unknown field, so such a document is an invalid selector.
    private static void StoredCaptionKeyIsInvalid()
    {
        var legacy = JsonNode.Parse("{\"role\":\"Edit\",\"name\":\"Field\",\"windowTitle\":\"Editor\"}");
        Require(!CompactStoredSelectorCodec.TryRead(legacy, false, out _), "a stored caption key is an invalid document");
        var kind = JsonNode.Parse("{\"role\":\"Edit\",\"name\":\"Field\",\"window\":\"main\"}");
        Require(CompactStoredSelectorCodec.TryRead(kind, false, out var selector) && selector.Value!.WindowKind == CompactWindowKind.Main,
            "a window kind is the stored owner");
    }

    // The identity of a verified control keeps its key when only its window's caption changes, while a different kind changes it.
    private static void CaptionFreeIdentity()
    {
        var first = Nodes(Row(1, "Window", "Fixture") + "\n" + Row(2, "button", "Save", depth: 2, autoId: "save"));
        var renamed = Nodes(Row(1, "Window", "*Fixture") + "\n" + Row(2, "button", "Save", depth: 2, autoId: "save"));
        var before = CompactSemanticIdentity.Target(10, first, first[1], Observed("Fixture", 100, "start"));
        var after = CompactSemanticIdentity.Target(10, renamed, renamed[1], Observed("*Fixture", 100, "start"));
        Require(CompactSemanticIdentity.StableKey(before) == CompactSemanticIdentity.StableKey(after), "identity key ignores captions");
        var selector = new CompactStoredSelector(CompactRole.Parse("button"), "Save", "save", CompactWindowKind.Main);
        Require(CompactDiscoveryFields.Key(selector) != CompactDiscoveryFields.Key(selector with
        {
            WindowKind = CompactWindowKind.Dialog
        }),
            "a different window kind is a different selector");
    }

    private static CompactNativeWindow Observed(string title, long handle, string started)
    {
        return new(10, "fixture", "1.0", title, handle, started, CompactWindowKind.Main);
    }

    private static string Row(int id, string role, string name, string? value = "", bool offscreen = false,
        int depth = 0, string autoId = "")
    {
        string enabled = "True";
        string hidden = offscreen ? "True" : "False";
        string quotedName = CompactJsonText.Serialize(System.Text.Json.Nodes.JsonValue.Create(name));
        string quotedAutoId = CompactJsonText.Serialize(System.Text.Json.Nodes.JsonValue.Create(autoId));
        string quotedValue = CompactJsonText.Serialize(System.Text.Json.Nodes.JsonValue.Create(value));

        return new string(' ', depth) + $"[{id}] {role} {quotedName} (autoId: {quotedAutoId}, val: {quotedValue}, "
            + $"enabled: {enabled}, offscreen: {hidden})";
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Compact tree regression failed: " + name);
        }
    }
}
