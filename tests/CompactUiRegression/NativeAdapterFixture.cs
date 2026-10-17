using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Platforms;
using KLaniBot.Transport.CompactUi;

// A declared synthetic transport fault is never presented as a real application's output or a model-performance result.
internal enum NativeAdapterFault
{
    None,
    Append,
    Empty,
    WrongTail,
    LostResponse,
    UnknownLedger,
    NoTextInterface,
    UncertainNoText,
    MissingOutcome
}

// This transport fixture owns field/window/ledger state independently of the executor and rejects stale snapshots before mutating it.
internal sealed class NativeAdapterFixture : ICompactNodeClient
{
    public NativeAdapterFault Fault
    {
        get; set;
    }
    public string? NoTextRefusalCode
    {
        get; set;
    }
    public string Field { get; private set; } = "old";
    public string Source { get; set; } = "original source";
    public string Receipt { get; private set; } = "old receipt";
    public string Started { get; set; } = "2026-10-08T00:00:00Z";
    public string Title { get; set; } = "Editor";
    public string Build { get; set; } = "1.0";
    public string NativeDigest { get; set; } = new('a', 64);
    public bool Dialog
    {
        get; set;
    }
    public bool DuplicateWindow
    {
        get; set;
    }
    // A closed Edit menu keeps its items offscreen beneath one onscreen menu item, as an application reports them until it opens.
    public bool ClosedMenu
    {
        get; set;
    }
    // A File menu whose Save item is offscreen; saving clears the title's unsaved marker, disables Save and fills the Field edit.
    public bool FileMenu
    {
        get; set;
    }
    public bool MenuSaved
    {
        get; set;
    }
    // Puts the duplicate window in a second process of the same program, so two applications list the same stored function.
    public bool SecondProcess
    {
        get; set;
    }
    // The second process reports another program and build when these are set, so two programs share one desktop.
    public string? SecondProcessName
    {
        get; set;
    }
    public string? SecondBuild
    {
        get; set;
    }

    // The node's kind word for the first window; a value outside main/dialog must make Info refuse the reply.
    public string WindowKind { get; set; } = "main";
    // The kind word of the second window when DuplicateWindow is set, as a node reports a dialog that blocks the first.
    public string SecondWindowKind { get; set; } = "main";
    // Every observation request, so that a refused selection can be shown to make no node call.
    public List<JsonObject> TreeRequests { get; } = [];
    public int CallCount
    {
        get; private set;
    }
    // The caption of the second window when DuplicateWindow is set; it reuses Title when unset.
    public string? DuplicateTitle
    {
        get; set;
    }
    public bool WrongRole
    {
        get; set;
    }
    public bool DuplicateField
    {
        get; set;
    }
    public bool DisabledField
    {
        get; set;
    }
    // An unnamed field has neither name nor automation ID, so only its window and container path identify it.
    public bool UnnamedField
    {
        get; set;
    }
    public bool Truncated
    {
        get; set;
    }
    public bool ExcludeTextSchema
    {
        get; set;
    }
    public bool HideAfterCommit
    {
        get; set;
    }
    public bool RenameAfterWrite
    {
        get; set;
    }
    public bool ChoicesComplete { get; set; } = true;
    public bool ChoicesNeedOpen
    {
        get; set;
    }
    public bool OpeningChangesSelection
    {
        get; set;
    }
    // A save control that is enabled only after the field differs from its initial text, as an editor with unsaved changes.
    public bool SaveNeedsEdit
    {
        get; set;
    }
    public bool SaveDisabled
    {
        get; set;
    }
    public bool SaveAbsent
    {
        get; set;
    }
    public bool DuplicateSave
    {
        get; set;
    }
    public bool DuplicateChoice
    {
        get; set;
    }
    public bool ReadOnly
    {
        get; set;
    }
    // A chord written as one string is refused by the node before any input leaves it.
    public bool RefuseChordKeys
    {
        get; set;
    }

    // A click refused by the node before dispatch proves that no input was sent.
    public bool RefuseClicksBeforeDispatch
    {
        get; set;
    }

    // A typed value that the node acknowledges but never applies, so the read-back finds the old value.
    public bool IgnoreTextWrites
    {
        get; set;
    }

    public bool LosePasteResponse
    {
        get; set;
    }
    public bool LoseSelectAllResponse
    {
        get; set;
    }
    public Action<JsonObject>? BeforeCapabilities
    {
        get; set;
    }
    // Program ids the node reports for Programmable App MCPs; null reports none, as an older node would.
    public string[]? ApplicationMcps
    {
        get; set;
    }
    public Action<int>? BeforeTree
    {
        get; set;
    }
    public Action? AfterWrite
    {
        get; set;
    }
    public Action<JsonObject>? BeforeMutation
    {
        get; set;
    }
    public List<Tool> Mutations { get; } = [];
    public List<JsonObject> MutationArguments { get; } = [];
    public List<Guid> StatusQueries { get; } = [];
    public Dictionary<Guid, JsonObject> Ledger { get; } = [];
    private int _snapshot;
    private string _clipboard = "";
    public string SelectedChoice { get; private set; } = "a";
    public bool IgnoreSelection
    {
        get; set;
    }

    private void Record(Tool tool, JsonObject arguments)
    {
        Mutations.Add(tool);
        MutationArguments.Add(arguments.DeepClone().AsObject());
    }

    public Task<JsonObject> CallAsync(Tool tool, JsonObject arguments, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        CallCount++;
        if (tool == Tool.GetMachineCapabilities)
        {
            return Task.FromResult(Capabilities(arguments));
        }
        if (tool == Tool.ListWindows)
        {
            var windows = new JsonArray(Window());
            if (DuplicateWindow)
            {
                windows.Add(Window(101));
            }
            return Task.FromResult(new JsonObject { ["success"] = true, ["windows"] = windows });
        }
        if (tool == Tool.GetUiTree)
        {
            TreeRequests.Add(arguments.DeepClone().AsObject());
            return Task.FromResult(Tree());
        }
        if (tool == Tool.GetActionStatus)
        {
            Guid identifier = Guid.Parse(arguments["request_id"]!.GetValue<string>());
            StatusQueries.Add(identifier);
            return Task.FromResult(Fault == NativeAdapterFault.UnknownLedger ? Response(false, "unknown")
                : Ledger.GetValueOrDefault(identifier)?.DeepClone().AsObject() ?? Response(false, "unknown"));
        }

        return Task.FromResult(Mutate(tool, arguments));
    }

    private JsonObject Capabilities(JsonObject arguments)
    {
        BeforeCapabilities?.Invoke(arguments);
        var capabilities = new JsonObject
        {
            ["nodeId"] = "fixture-node",
            ["version"] = "synthetic-node-1",
            ["platform"] = "linux-x11-atspi",
            ["nativeImplementationSha256"] = NativeDigest
        };
        if (ApplicationMcps != null)
        {
            capabilities["applicationMcps"] = new JsonArray(ApplicationMcps.Select(id => (JsonNode?)id).ToArray());
        }
        if (arguments.ContainsKey("action_schema_names"))
        {
            var available = ToolContract.Tools.OfType<JsonObject>().Where(tool =>
                !ExcludeTextSchema || tool["name"]!.ToString() != "type_text");
            Require(SelectedActionSchemas.TrySelect(arguments["action_schema_names"], available, out var schemas),
                "valid schema selection");
            capabilities["actionSchemas"] = schemas;
        }

        return new JsonObject { ["success"] = true, ["capabilities"] = capabilities };
    }

    private JsonObject Window(long handle = 100)
    {
        return new JsonObject
        {
            ["ProcessId"] = handle != 100 && SecondProcess ? 11 : 10,
            ["ProcessName"] = handle != 100 && SecondProcess ? SecondProcessName ?? "fixture-editor" : "fixture-editor",
            ["AppVersion"] = handle != 100 && SecondProcess ? SecondBuild ?? Build : Build,
            ["Title"] = handle != 100 && DuplicateTitle != null ? DuplicateTitle : Title,
            ["WindowHandle"] = handle,
            ["Kind"] = handle == 100 ? WindowKind : SecondWindowKind,
            ["ProcessStartedAtUtc"] = Started
        };
    }

    private JsonObject Tree()
    {
        _snapshot++;
        BeforeTree?.Invoke(_snapshot);
        string tree = Row(0, 1, "Window", Title, "", "") + Row(2, 2, "Button", "Open", "open", "")
            + Row(2, 4, "Text", "Source", "source", Source) + Row(2, 5, "Text", "Receipt", "receipt", Receipt)
            + Row(2, 6, "Button", "Commit", "commit", "") + Row(2, 7, "combo box", "Choice", "choice", "");
        if (Dialog)
        {
            tree += UnnamedField ? Row(2, 3, "Edit", "", "", Field, !DisabledField)
                : Row(2, 3, WrongRole ? "Button" : "Edit", "Field", "field", Field, !DisabledField);
            if (DuplicateField)
            {
                tree += Row(2, 8, "Edit", "Field", "field", Field);
            }
        }
        if (!SaveAbsent)
        {
            tree += Row(2, 9, "Button", "Save", "save", "", SaveEnabled());
            if (DuplicateSave)
            {
                tree += Row(2, 10, "Button", "Save", "save", "", SaveEnabled());
            }
        }
        tree += MenuRows();
        var fields = FieldMetadata();

        return new JsonObject
        {
            ["success"] = true,
            ["snapshot_id"] = "s" + _snapshot,
            ["tree"] = tree,
            ["truncated"] = Truncated,
            ["form_metadata"] = JsonSerializer.SerializeToNode(fields)
        };
    }

    // The menu rows come after the main controls: a closed Edit menu, then the File menu with its Save item when it is requested.
    private string MenuRows()
    {
        string rows = "";
        if (ClosedMenu)
        {
            rows += Row(2, 11, "menu item", "Edit", "", "") + Row(4, 12, "menu item", "Select All", "", "", true, true)
                + Row(4, 13, "menu item", "Convert", "", "", true, true) + Row(6, 14, "menu item", "To Uppercase", "", "", true, true);
        }
        if (FileMenu)
        {
            rows += Row(2, 15, "menu item", "File", "", "") + Row(4, 16, "menu item", "Save", "", "", !MenuSaved, true);
        }

        return rows;
    }

    private bool SaveEnabled()
    {
        return !SaveDisabled && (!SaveNeedsEdit || Field != "old");
    }

    private IReadOnlyDictionary<int, UiFieldMetadata> FieldMetadata()
    {
        var options = new List<UiChoiceOption> { new("a", "First", true), new("b", "Second", true) };
        if (DuplicateChoice)
        {
            options.Add(new("c", "Second", true));
        }
        var choices = new UiChoiceMetadata(UiFormSupport.Available, "choice-revision", options.ToArray(),
            ChoicesComplete, ChoicesNeedOpen, SelectedChoice);
        var metadata = new UiFieldMetadata(UiFormSupport.Available, UiFieldMetadataSource.AtSpiState, true, true, ReadOnly,
            null, "context", null, null, choices);
        var fields = new Dictionary<int, UiFieldMetadata>
        {
            [7] = metadata,
            [3] = metadata with
            {
                ReadOnly = ReadOnly,
                Enabled = !DisabledField,
                Choices = UiChoiceMetadata.Unsupported
            }
        };

        return fields;
    }

    private JsonObject Mutate(Tool tool, JsonObject arguments)
    {
        BeforeMutation?.Invoke(arguments);
        if (arguments.ContainsKey("id"))
        {
            Require(arguments["snapshot_id"]!.ToString() == "s" + _snapshot, "fresh snapshot used at send");
        }
        Guid identifier = Guid.Parse(arguments["request_id"]!.GetValue<string>());
        Require(Uuid7Time.TryReadMilliseconds(identifier.ToString(), out _), "native-compatible original ID");
        if (tool == Tool.TypeText && Fault is NativeAdapterFault.NoTextInterface or NativeAdapterFault.UncertainNoText)
        {
            var refused = Response(false, Fault == NativeAdapterFault.NoTextInterface ? "not_dispatched" : "unknown");
            refused["message"] = "Target has no editable text interface.";
            if (NoTextRefusalCode != null)
            {
                refused["code"] = NoTextRefusalCode;
            }
            Ledger.Add(identifier, refused);
            return refused.DeepClone().AsObject();
        }
        if (RefusedBeforeDispatch(tool, arguments, identifier) is JsonObject refusal)
            return refusal;
        Record(tool, arguments);
        ApplyUnlessIgnored(tool, arguments);
        var completed = Response(true, "completed");
        Ledger.Add(identifier, completed);
        if (tool == Tool.Hotkey && (LosePasteResponse && arguments["keys"]!.AsArray().Last()!.ToString() == "v"
            || LoseSelectAllResponse && arguments["keys"]!.AsArray().Last()!.ToString() == "a"))
        {
            return Response(false, "unknown");
        }
        if (tool == Tool.TypeText && Fault is NativeAdapterFault.LostResponse or NativeAdapterFault.UnknownLedger)
        {
            return Response(false, "unknown");
        }
        if (Fault == NativeAdapterFault.MissingOutcome)
        {
            completed.Remove("outcome");
        }

        return completed.DeepClone().AsObject();
    }

    // Each synthetic control reacts to its own identifier, so a test names the effect of a click exactly.
    private void ApplyClick(JsonObject arguments)
    {
        int id = arguments["id"]!.GetValue<int>();
        if (id == 2)
        {
            Dialog = true;
        }
        if (id == 16)
        {
            ApplySave();
        }
        if (id == 6)
        {
            Receipt = "committed " + Field;
            if (HideAfterCommit)
            {
                Dialog = false;
            }
        }
        if (id == 7 && !IgnoreSelection)
        {
            ApplyChoice(arguments);
        }
    }

    // Saving clears the unsaved marker, disables the Save item, and leaves the field holding the saved text.
    private void ApplySave()
    {
        MenuSaved = true;
        Title = Title.Replace("*", "", StringComparison.Ordinal);
        Field = "saved";
    }

    private void Apply(Tool tool, JsonObject arguments)
    {
        if (tool == Tool.TypeText)
        {
            string text = arguments["text"]!.GetValue<string>();
            Field = Fault == NativeAdapterFault.Append ? "old" + text : Fault == NativeAdapterFault.Empty ? ""
                : Fault == NativeAdapterFault.WrongTail ? text[..^1] + "y" : text;
            if (RenameAfterWrite)
            {
                Title = "Editor *";
            }
            AfterWrite?.Invoke();
        }
        if (tool == Tool.ClickElement)
        {
            ApplyClick(arguments);
        }
        if (tool == Tool.SetClipboard)
        {
            _clipboard = arguments["text"]!.GetValue<string>();
        }
        if (tool == Tool.Hotkey && arguments["keys"]!.AsArray().Last()!.ToString() == "v")
        {
            Field = _clipboard;
        }
    }

    private void ApplyChoice(JsonObject arguments)
    {
        if (arguments["choice_action"]?.ToString() == "open")
        {
            ChoicesNeedOpen = false;
            if (OpeningChangesSelection)
            {
                SelectedChoice = "b";
            }
            return;
        }

        SelectedChoice = arguments["option_id"]!.GetValue<string>();
    }

    public void ForceField(string value)
    {
        Field = value;
    }

    private static string Row(int depth, int id, string role, string name, string autoId, string value, bool enabled = true,
        bool offscreen = false)
    {
        return new string(' ', depth) + $"[{id}] {role} {JsonSerializer.Serialize(name)} (autoId: {JsonSerializer.Serialize(autoId)}, "
            + $"val: {JsonSerializer.Serialize(value)}, enabled: {enabled}, offscreen: {offscreen})\n";
    }

    // A chord written as one string is refused before any input leaves the fake node, with the node's own unsupported-key text.
    // The fake node acknowledges an ignored text write, yet the field keeps its old value.
    private void ApplyUnlessIgnored(Tool tool, JsonObject arguments)
    {
        if (!IgnoreTextWrites || tool != Tool.TypeText)
        {
            Apply(tool, arguments);
        }
    }

    private JsonObject? RefusedBeforeDispatch(Tool tool, JsonObject arguments, Guid identifier)
    {
        string? error = ChordError(tool, arguments);
        if (error == null && tool == Tool.ClickElement && RefuseClicksBeforeDispatch)
        {
            error = "Refused before dispatch.";
        }
        if (error == null)
        {
            return null;
        }
        var refused = Response(false, "not_dispatched");
        refused["error"] = error;
        Ledger.Add(identifier, refused);

        return refused.DeepClone().AsObject();
    }

    private string? ChordError(Tool tool, JsonObject arguments)
    {
        if (tool != Tool.Hotkey || !RefuseChordKeys || !arguments["keys"]!.AsArray().Any(key => key!.ToString().Contains('+')))
        {
            return null;
        }

        return "Unsupported key: " + string.Join("+", arguments["keys"]!.AsArray().Select(key => key!.ToString()));
    }

    private static JsonObject Response(bool success, string outcome)
    {
        return new JsonObject { ["success"] = success, ["outcome"] = outcome };
    }

    private static void Require(bool condition, string name)
    {
        WorkflowEngineSyntheticAdapter.Require(condition, name);
    }
}
