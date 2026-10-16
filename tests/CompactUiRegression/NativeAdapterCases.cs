using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Transport.CompactUi;

// Actual native services and private stores run against an independent synthetic transport state oracle, without real-app
// acceptance claims.
internal static class NativeAdapterCases
{
    public static async Task RunAsync()
    {
        SelectedSchemas();
        await SuccessfulWritesAsync();
        await RejectedWritesAsync();
        await RejectedTargetsAsync();
        await KeyboardRouteAsync();
        await KeyboardResumeAsync();
        await NativeWorkflowAsync();
        await ResumeExactValuesAsync();
        await RefuseChangedSourcesAsync();
        await ChangedFieldBeforeCommitAsync();
        await NativeChoicesAsync();
        UnnamedTargetQualification();
        await UnnamedKeyboardRouteAsync();
        await UnsavedMarkerKeepsWindowIdentityAsync();
        await WindowKindChoiceAsync();
        await StepKindsReadTheirOwnWindowAsync();
        await EarlierStepEnablesTargetAsync();
        await EarlierStepLeavesTargetDisabledAsync();
        await AbsentOrAmbiguousLaterTargetAsync();
        await LegacyDisabledTargetAsync();
        await WindowSelectionAsync();
        await UnknownWindowKindAsync();
    }

    private static void SelectedSchemas()
    {
        var permitted = new[] { ToolContract.Get(Tool.GetMachineCapabilities) };
        Require(SelectedActionSchemas.TrySelect(new JsonArray("get_machine_capabilities", "type_text"), permitted, out var selected)
            && selected!.Count == 1, "unavailable schemas never grant access");
        Require(!SelectedActionSchemas.TrySelect(new JsonArray("type_text", "type_text"), permitted, out _),
            "duplicate schema names refused");
        Require(!SelectedActionSchemas.TrySelect(new JsonArray("invented"), permitted, out _), "unknown schema names refused");
        Require(!SelectedActionSchemas.TrySelect(new JsonArray(12), permitted, out _), "schema names are typed boundary strings");
        Require(SelectedActionSchemas.TrySelect(new JsonArray(), permitted, out var empty) && empty!.Count == 0,
            "explicit empty selection");
        Require(CompactNodeResponse.Read(new JsonObject { ["success"] = true }).Outcome == ActionOutcome.Unknown,
            "missing mutation outcome remains unknown");
    }

    private static async Task SuccessfulWritesAsync()
    {
        foreach (string expected in new[] { "new", "", new string('x', 16000) })
        {
            using var test = new NativeAdapterEnvironment();
            test.Client.Dialog = true;
            var function = test.Write();
            var prepared = await PrepareAsync(test, function);
            var output = await test.Executor.ExecuteAsync(prepared, Parameters(expected), null, null, CancellationToken.None);
            Require(test.Client.Field == expected && output.Outputs["value"] == expected, "full field oracle and returned output agree");
            Require(test.Client.Mutations.SequenceEqual([Tool.TypeText]), "one literal replacement without Enter");
        }
        using var renamed = new NativeAdapterEnvironment();
        renamed.Client.Dialog = true;
        renamed.Client.RenameAfterWrite = true;
        var changed = await PrepareAsync(renamed, renamed.Write());
        var result = await renamed.Executor.ExecuteAsync(changed, Parameters("new"), null, null, CancellationToken.None);
        Require(result.Outputs["value"] == "new" && renamed.Client.Title == "Editor *", "same proved window survives caption change");
        using var closing = new NativeAdapterEnvironment();
        closing.Client.Dialog = true;
        closing.Client.HideAfterCommit = true;
        var submit = await PrepareAsync(closing, closing.Write(commit: true));
        var receipt = await closing.Executor.ExecuteAsync(submit, Parameters("submitted"), null, null, CancellationToken.None);
        Require(receipt.Outputs["value"] == "committed submitted" && !closing.Client.Dialog,
            "verified setter may precede a later action that removes its field");
    }

    private static async Task RejectedWritesAsync()
    {
        foreach (var fault in new[] { NativeAdapterFault.Append, NativeAdapterFault.Empty, NativeAdapterFault.WrongTail,
            NativeAdapterFault.MissingOutcome })
        {
            using var test = new NativeAdapterEnvironment();
            test.Client.Dialog = true;
            test.Client.Fault = fault;
            var prepared = await PrepareAsync(test, test.Write(commit: true));
            string expected = fault == NativeAdapterFault.WrongTail ? new string('x', 16000) : "new";
            await RefusedAsync(() => test.Executor.ExecuteAsync(prepared, Parameters(expected), null, null, CancellationToken.None));
            Require(test.Client.Mutations.SequenceEqual([Tool.TypeText]) && test.Client.Receipt == "old receipt",
                "mismatch/missing outcome prevents the later commit action");
        }
        using var replaced = new NativeAdapterEnvironment();
        replaced.Client.Dialog = true;
        var original = await PrepareAsync(replaced, replaced.Write(commit: true));
        replaced.Client.AfterWrite = () => replaced.Client.Started = "replacement";
        await RefusedAsync(() => replaced.Executor.ExecuteAsync(original, Parameters("new"), null, null, CancellationToken.None));
        Require(replaced.Client.Mutations.Count == 1, "replaced process stops before later action");
    }

    private static async Task RejectedTargetsAsync()
    {
        string[] faults = ["missing", "role", "ambiguous", "disabled", "truncated", "window", "unknown", "schema", "implementation"];
        foreach (string fault in faults)
        {
            using var test = new NativeAdapterEnvironment();
            test.Client.Dialog = fault != "missing";
            test.Client.WrongRole = fault == "role";
            test.Client.DuplicateField = fault == "ambiguous";
            test.Client.DisabledField = fault == "disabled";
            test.Client.Truncated = fault == "truncated";
            test.Client.DuplicateWindow = fault == "window";
            test.Client.Build = fault == "unknown" ? "unknown" : "1.0";
            test.Client.ExcludeTextSchema = fault == "schema";
            var function = test.Write();
            if (fault == "implementation")
            {
                test.Client.NativeDigest = new string('c', 64);
            }
            await RefusedAsync(() => PrepareAsync(test, function));
            Require(test.Client.Mutations.Count == 0, fault + " preflight sends no input");
        }
    }

    private static async Task KeyboardRouteAsync()
    {
        using var test = new NativeAdapterEnvironment();
        test.Client.Dialog = true;
        test.Client.Fault = NativeAdapterFault.NoTextInterface;
        var prepared = await PrepareAsync(test, test.Write(route: CompactRecipeRoute.FocusKeyboard));
        var result = await test.Executor.ExecuteAsync(prepared, Parameters("pasted"), null, null, CancellationToken.None);
        Require(result.Outputs["value"] == "pasted" && test.Client.Field == "pasted", "recorded keyboard route verifies complete value");
        Require(test.Client.Mutations.SequenceEqual([Tool.ClickElement, Tool.SetClipboard, Tool.Hotkey, Tool.Hotkey]),
            "previously qualified keyboard route uses semantic focus and documented keys");
        using var unknown = new NativeAdapterEnvironment();
        unknown.Client.Dialog = true;
        unknown.Client.Fault = NativeAdapterFault.UncertainNoText;
        var ambiguous = await PrepareAsync(unknown, unknown.Write());
        await RefusedAsync(() => unknown.Executor.ExecuteAsync(ambiguous, Parameters("never"), null, null, CancellationToken.None));
        Require(unknown.Client.Mutations.Count == 0, "unknown native setter cannot trigger fallback or a duplicate input");
    }

    // A recorded keyboard route never writes an unnamed field: the refusal comes before any click, clipboard or hotkey is sent.
    private static async Task UnnamedKeyboardRouteAsync()
    {
        using var test = new NativeAdapterEnvironment();
        test.Client.Dialog = true;
        test.Client.UnnamedField = true;
        test.Client.Fault = NativeAdapterFault.NoTextInterface;
        var unnamed = NativeAdapterEnvironment.Selector("Edit", "", "");
        var steps = new[] { new CompactRecipeStep(CompactRecipeAction.Write, unnamed, CompactRecipeRoute.FocusKeyboard, "text") };
        var outputs = new[] { new CompactRecipeOutput("value", unnamed, CompactOutputField.Value, CompactOutputCheckKind.Exists) };
        var prepared = await PrepareAsync(test, test.Install("unnamed_keyboard", steps, outputs));
        await RefusedWithAsync(() => test.Executor.ExecuteAsync(prepared, Parameters("never"), null, null, CancellationToken.None),
            CompactRecipeTarget.UnnamedTextRefusal, "a recorded keyboard route refuses an unnamed field");
        Require(test.Client.Mutations.Count == 0, "the refused keyboard route sends no click, clipboard or hotkey");
    }

    // A refusal must be a CompactNodeException with the exact message; a different failure cannot satisfy the check.
    private static async Task RefusedWithAsync<T>(Func<Task<T>> action, string message, string name)
    {
        try
        {
            await action();
        }
        catch (CompactNodeException exception)
        {
            Require(exception.Message == message, name + ": " + exception.Message);

            return;
        }
        Require(false, name);
    }

    private static async Task KeyboardResumeAsync()
    {
        foreach (bool complete in new[] { true, false })
        {
            using var test = new NativeAdapterEnvironment();
            test.Client.Dialog = true;
            test.Client.Fault = NativeAdapterFault.NoTextInterface;
            test.Client.LosePasteResponse = complete;
            test.Client.LoseSelectAllResponse = !complete;
            test.Write(route: CompactRecipeRoute.FocusKeyboard);
            var engine = test.Workflow(NativeAdapterEnvironment.Definition(), out _);
            var run = await engine.RunAsync("fixture", 1, "keyboard", new JsonObject { ["text"] = "pasted" });
            Require(run.Value!.Status == CompactWorkflowStatus.Unconfirmed, "lost keyboard response remains unconfirmed");
            int mutations = test.Client.Mutations.Count;
            var resume = await engine.ResumeAsync(run.Value.Reference, run.Value.Revision);
            Require(resume.Value!.Status == (complete ? CompactWorkflowStatus.Completed : CompactWorkflowStatus.Unconfirmed),
                "only a complete original fallback plus exact field can reconcile");
            Require(test.Client.Mutations.Count == mutations, "resume never sends the missing keyboard suffix");
        }
    }

    private static async Task ChangedFieldBeforeCommitAsync()
    {
        using var test = new NativeAdapterEnvironment();
        test.Client.Dialog = true;
        var prepared = await PrepareAsync(test, test.Write(commit: true));
        test.Client.BeforeTree = index =>
        {
            if (index == 4)
            {
                test.Client.ForceField("changed between verification and next action");
            }
        };
        await RefusedAsync(() => test.Executor.ExecuteAsync(prepared, Parameters("new"), null, null, CancellationToken.None));
        Require(test.Client.Mutations.SequenceEqual([Tool.TypeText]) && test.Client.Receipt == "old receipt",
            "fresh field drift prevents the later commit");
    }

    private static async Task NativeChoicesAsync()
    {
        foreach (string fault in new[] { "none", "incomplete", "ambiguous", "readonly", "ignored" })
        {
            using var test = new NativeAdapterEnvironment();
            test.Client.ChoicesComplete = fault != "incomplete";
            test.Client.DuplicateChoice = fault == "ambiguous";
            test.Client.ReadOnly = fault == "readonly";
            test.Client.IgnoreSelection = fault == "ignored";
            var selector = NativeAdapterEnvironment.Selector("combo box", "Choice", "choice");
            var function = test.Install("choice", [new(CompactRecipeAction.Select, selector, CompactRecipeRoute.NativeChoice, "choice")],
                [new("value", selector, CompactOutputField.Value, CompactOutputCheckKind.EqualsParameter, "choice")]);
            var prepared = await PrepareAsync(test, function);
            var parameters = new Dictionary<string, string> { ["choice"] = "Second" };
            if (fault == "none")
            {
                var result = await test.Executor.ExecuteAsync(prepared, parameters, null, null, CancellationToken.None);
                Require(test.Client.SelectedChoice == "b" && result.Outputs["value"] == "Second",
                    "native choice ID and oracle label agree");
                continue;
            }
            await RefusedAsync(() => test.Executor.ExecuteAsync(prepared, parameters, null, null, CancellationToken.None));
            Require(test.Client.SelectedChoice == "a",
                "choice refusal or ignored dispatch preserves the independent selected-state oracle");
            Require(test.Client.Mutations.Count == (fault == "ignored" ? 1 : 0), "invalid option metadata sends no input");
        }
    }

    // The unnamed rule is general: a text write into a plain text entry bound to its window and container path succeeds in any
    // application, while clicks, other roles, password fields and unknown container paths stay refused. The resolver still requires
    // exactly one match, and the read-back must show the complete written value.
    private static void UnnamedTargetQualification()
    {
        UnnamedCalculatorWrites();
        UnnamedWriteRefusals();
        UnnamedTextAreaWrite();
    }

    // Two unnamed entries in different container paths are each reached by their own path, and only a complete read-back verifies.
    private static void UnnamedCalculatorWrites()
    {
        var calculator = new CompactNativeWindow(10, "gnome-calculator", "46.3", "Calculator", 100, "synthetic-lifetime",
            CompactWindowKind.Main);
        var observed = Observation(CalculatorRows("0", ""));
        var panel = new CompactContainerSelector(CompactRole.Parse("Panel"), null, "");
        var scroll = new CompactContainerSelector(CompactRole.Parse("Scroll pane"), null, "");
        var inputLine = UnnamedEdit([panel, scroll]);
        var secondLine = UnnamedEdit([panel, panel]);
        Require(IdOf(CompactRecipeTarget.Require(observed, inputLine, calculator, CompactRecipeAction.Write)) == 4,
            "a write reaches the input line of a non-Mousepad application");
        Require(IdOf(CompactRecipeTarget.Require(observed, secondLine, calculator, CompactRecipeAction.Write)) == 7,
            "a write reaches the second line through its own container path");
        Require(CompactWritePostcondition.TryCheck(CalculatorRows("6*7", ""), inputLine, "6*7", out _)
            && CompactWritePostcondition.TryCheck(CalculatorRows("6*7", ""), secondLine, "", out _),
            "the read-back verifies the written line and leaves the other line unchanged");
        Require(!CompactWritePostcondition.TryCheck(CalculatorRows("0", ""), inputLine, "6*7", out var unchanged)
            && unchanged.Error == CompactCaptureError.WrittenValueMismatch, "an unchanged read-back is a written-value mismatch");
        Require(CompactCaptureNames.Error(CompactCaptureError.WrittenValueMismatch)
            == "The observed value does not match the written input.", "the mismatch keeps its exact message");
    }

    // Refusals name the unnamed-target rule, and an ambiguous or clicked unnamed target never reaches the node.
    private static void UnnamedWriteRefusals()
    {
        var calculator = new CompactNativeWindow(10, "gnome-calculator", "46.3", "Calculator", 100, "synthetic-lifetime",
            CompactWindowKind.Main);
        var observed = Observation(CalculatorRows("0", ""));
        var duplicated = Observation(TreeRow(0, 1, "Window", "Calculator", null), TreeRow(2, 2, "Edit", "", "0"),
            TreeRow(2, 3, "Edit", "", ""));
        Refused(() => CompactRecipeTarget.Require(duplicated, UnnamedEdit([]), calculator, CompactRecipeAction.Write),
            "two unnamed entries with one container path are ambiguous");
        var button = Observation(TreeRow(0, 1, "Window", "Calculator", null), TreeRow(2, 2, "Button", "", null));
        var buttonSelector = new CompactStoredSelector(CompactRole.Parse("Button"), "", "", CompactWindowKind.Main, []);
        RefusedWith(() => CompactRecipeTarget.Require(button, buttonSelector, calculator, CompactRecipeAction.Click),
            CompactRecipeTarget.UnnamedRefusal, "an unnamed button is never clicked");
        RefusedWith(() => CompactRecipeTarget.Require(button, buttonSelector, calculator, CompactRecipeAction.Write),
            CompactRecipeTarget.UnnamedRefusal, "an unnamed button is never written");
        var choice = Observation(TreeRow(0, 1, "Window", "Calculator", null), TreeRow(2, 2, "combo box", "", "a"));
        var choiceSelector = new CompactStoredSelector(CompactRole.Parse("combo box"), "", "", CompactWindowKind.Main, []);
        RefusedWith(() => CompactRecipeTarget.Require(choice, choiceSelector, calculator, CompactRecipeAction.Select),
            CompactRecipeTarget.UnnamedRefusal, "an unnamed choice is never selected");
        RefusedWith(() => CompactRecipeTarget.Require(observed, UnnamedEdit(null), calculator, CompactRecipeAction.Write),
            CompactRecipeTarget.UnnamedRefusal, "an unnamed write without a known container path is refused");
        var password = Observation(TreeRow(0, 1, "Window", "Calculator", null), TreeRow(2, 2, "password text", "", ""));
        var passwordSelector = new CompactStoredSelector(CompactRole.Parse("password text"), "", "", CompactWindowKind.Main, []);
        RefusedWith(() => CompactRecipeTarget.Require(password, passwordSelector, calculator, CompactRecipeAction.Write),
            CompactRecipeTarget.UnnamedRefusal, "a password field is never written");
    }

    // An unnamed text area directly under its window (empty container path) takes a write in any application; the read-back decides.
    private static void UnnamedTextAreaWrite()
    {
        var editor = new CompactNativeWindow(11, "gedit", "1.0", "TextEdit", 200, "synthetic-lifetime", CompactWindowKind.Main);
        var textArea = Observation(TreeRow(0, 1, "Window", "TextEdit", null), TreeRow(2, 2, "TextArea", "", ""));
        var areaSelector = new CompactStoredSelector(CompactRole.Parse("TextArea"), "", "", CompactWindowKind.Main, []);
        Require(IdOf(CompactRecipeTarget.Require(textArea, areaSelector, editor, CompactRecipeAction.Write)) == 2,
            "an unnamed text area directly under its window accepts a write");
        CompactTreeNode[] writtenArea = [TreeRow(0, 1, "Window", "TextEdit", null), TreeRow(2, 2, "TextArea", "", "Hello")];
        Require(CompactWritePostcondition.TryCheck(writtenArea, areaSelector, "Hello", out _),
            "the text area read-back verifies the complete written value");
        var orphan = Observation(TreeRow(2, 2, "TextArea", "", ""));
        RefusedWith(() => CompactRecipeTarget.Require(orphan, areaSelector, editor, CompactRecipeAction.Write),
            CompactRecipeTarget.UnnamedRefusal, "an unnamed text area with no window row above it is refused");
    }

    // A calculator-like window: the input line sits under a scroll pane, and a second unnamed line sits under another panel.
    private static CompactTreeNode[] CalculatorRows(string inputValue, string secondValue)
    {
        return
        [
            TreeRow(0, 1, "Window", "Calculator", null),
            TreeRow(2, 2, "Panel", "", null),
            TreeRow(4, 3, "Scroll pane", "", null),
            TreeRow(6, 4, "Edit", "", inputValue),
            TreeRow(2, 5, "Panel", "", null),
            TreeRow(4, 6, "Panel", "", null),
            TreeRow(6, 7, "Edit", "", secondValue)
        ];
    }

    private static CompactTreeNode TreeRow(int depth, int id, string role, string name, string? value)
    {
        return new(depth, id, CompactRole.Parse(role), name, "", value, true, false);
    }

    private static CompactNativeObservation Observation(params CompactTreeNode[] nodes)
    {
        return new("fresh", nodes, new Dictionary<int, KLaniBot.Platforms.UiFieldMetadata>());
    }

    private static CompactStoredSelector UnnamedEdit(IReadOnlyList<CompactContainerSelector>? containerPath)
    {
        return new(CompactRole.Parse("Edit"), "", "", CompactWindowKind.Main, containerPath);
    }

    private static int IdOf(JsonObject target)
    {
        return target["id"]!.GetValue<int>();
    }

    // Refusals must be CompactNodeException with the exact message, so an unrelated failure cannot satisfy the check.
    private static void RefusedWith(Action action, string message, string name)
    {
        try
        {
            action();
        }
        catch (CompactNodeException exception)
        {
            Require(exception.Message == message, name + ": " + exception.Message);

            return;
        }
        Require(false, name);
    }

    private static void Refused(Action action, string name)
    {
        try
        {
            action();
        }
        catch (CompactNodeException)
        {
            return;
        }
        Require(false, name);
    }

    private static async Task NativeWorkflowAsync()
    {
        using var test = new NativeAdapterEnvironment();
        test.Open();
        test.Write();
        var engine = test.Workflow(NativeAdapterEnvironment.Definition(open: true), out var store);
        test.Client.Fault = NativeAdapterFault.LostResponse;
        test.Client.BeforeMutation = arguments =>
        {
            string identifier = arguments["request_id"]!.GetValue<string>();
            string file = Directory.GetFiles(store.Root, "*.json").Single();
            Require(File.ReadAllText(file).Contains(identifier, StringComparison.Ordinal),
                "original intent durable before actual client send");
        };
        var interrupted = await engine.RunAsync("fixture", 1, "request", new JsonObject { ["text"] = "new" });
        Require(interrupted.Value!.Status == CompactWorkflowStatus.Unconfirmed && test.Client.Mutations.Count == 2,
            "actual adapter preserves lost response after field mutation");
        Require(store.TryRead(interrupted.Value.Reference, out var state), "read native checkpoints");
        Guid original = state.Value!.Job.Pending!.Operations.Single().RequestId;
        Require(state.Value.Job.AdapterState!["function_hashes"]!["write"] != null, "prepared function metadata is persisted before input");
        var restarted = test.Workflow(NativeAdapterEnvironment.Definition(open: true), out _);
        var resumed = await restarted.ResumeAsync(interrupted.Value.Reference, interrupted.Value.Revision);
        Require(resumed.Value!.Status == CompactWorkflowStatus.Completed && test.Client.Mutations.Count == 2,
            "restart verifies actual field without replay");
        Require(test.Client.StatusQueries.SequenceEqual([original]), "resume queries exact original ledger ID");
    }

    private static async Task ResumeExactValuesAsync()
    {
        foreach (var fault in new[] { NativeAdapterFault.Append, NativeAdapterFault.Empty, NativeAdapterFault.WrongTail,
            NativeAdapterFault.UnknownLedger })
        {
            using var test = new NativeAdapterEnvironment();
            test.Client.Dialog = true;
            test.Client.Fault = fault;
            test.Write();
            var engine = test.Workflow(NativeAdapterEnvironment.Definition(), out _);
            string expected = fault == NativeAdapterFault.WrongTail ? new string('x', 16000) : "new";
            var run = await engine.RunAsync("fixture", 1, "exact", new JsonObject { ["text"] = expected });
            Require(run.Value!.Status == CompactWorkflowStatus.Unconfirmed, "bad replacement is unconfirmed");
            var resume = await engine.ResumeAsync(run.Value.Reference, run.Value.Revision);
            Require(resume.Value!.Status == CompactWorkflowStatus.Unconfirmed && test.Client.Mutations.Count == 1,
                "Exists output/ledger success cannot confirm append, empty, clipped prefix or missing ledger");
        }
    }

    private static async Task RefuseChangedSourcesAsync()
    {
        using var test = new NativeAdapterEnvironment();
        test.Client.Dialog = true;
        test.Write();
        test.Install("read", [new(CompactRecipeAction.Read, NativeAdapterEnvironment.Selector("Text", "Source", "source"),
            CompactRecipeRoute.NativeRead)], [new("value", NativeAdapterEnvironment.Selector("Text", "Source", "source"),
            CompactOutputField.Value, CompactOutputCheckKind.NotEmpty)]);
        var definition = NativeAdapterEnvironment.Definition();
        definition["inputs"] = new JsonObject { ["confirm"] = new JsonObject { ["type"] = "boolean" } };
        var target = definition["stages"]![0]!.AsObject();
        var read = target.DeepClone().AsObject();
        read["id"] = "source";
        read["function_ref"]!["name"] = "read";
        read["bindings"] = new JsonObject();
        definition["stages"]!.AsArray().Insert(0, read);
        target["bindings"]!["text"] = new JsonObject { ["stage"] = "source", ["output"] = "value" };
        target["decision"] = new JsonObject { ["input"] = "confirm", ["prompt"] = "Transfer the observed source?" };
        var engine = test.Workflow(definition, out _);
        var awaiting = await engine.RunAsync("fixture", 1, "source", new JsonObject());
        Require(awaiting.Value!.Status == CompactWorkflowStatus.AwaitingInput && test.Client.Mutations.Count == 0,
            "read and explicit decision separate");
        test.Client.Source = "changed source";
        var refused = await engine.ResumeAsync(awaiting.Value.Reference, awaiting.Value.Revision, new JsonObject { ["confirm"] = true });
        Require(refused.Value!.Status == CompactWorkflowStatus.Blocked && test.Client.Mutations.Count == 0,
            "changed source digest refuses every target input");
    }

    // An editor shows "*" in the caption of the same window while it has unsaved changes. A recipe recorded in either state
    // must bind before and after an edit; another document, a doubled marker or two captions of one name stay refused.
    private static async Task UnsavedMarkerKeepsWindowIdentityAsync()
    {
        using var modified = new NativeAdapterEnvironment();
        modified.Client.Dialog = true;
        modified.Client.Title = "*Editor";
        var plainRecipe = await PrepareAsync(modified, modified.Write());
        var first = await modified.Executor.ExecuteAsync(plainRecipe, Parameters("new"), null, null, CancellationToken.None);
        Require(first.Outputs["value"] == "new" && modified.Client.Field == "new", "plain recipe binds the modified window");

        using var saved = new NativeAdapterEnvironment();
        saved.Client.Dialog = true;
        var captionRecipe = await PrepareAsync(saved, saved.Write());
        saved.Client.Title = "*Editor";
        var second = await saved.Executor.ExecuteAsync(captionRecipe, Parameters("again"), null, null, CancellationToken.None);
        Require(second.Outputs["value"] == "again" && saved.Client.Field == "again", "a changed caption keeps the recipe on its kind");

        using var editing = new NativeAdapterEnvironment();
        editing.Client.Dialog = true;
        editing.Client.AfterWrite = () => editing.Client.Title = "*" + editing.Client.Title;
        var crossing = await PrepareAsync(editing, editing.Write());
        var edited = await editing.Executor.ExecuteAsync(crossing, Parameters("edited"), null, null, CancellationToken.None);
        Require(edited.Outputs["value"] == "edited" && editing.Client.Title == "*Editor",
            "a write that adds the marker is verified, not failed");

        // The caption is never identity: a recipe bound by window kind follows its window through any caption change.
        foreach (string renamed in new[] { "Other", "**Editor", "Editor *" })
        {
            using var other = new NativeAdapterEnvironment();
            other.Client.Dialog = true;
            other.Client.Title = renamed;
            var renamedRecipe = await PrepareAsync(other, other.Write());
            var renamedResult = await other.Executor.ExecuteAsync(renamedRecipe, Parameters("renamed"), null, null, CancellationToken.None);
            Require(renamedResult.Outputs["value"] == "renamed" && other.Client.Field == "renamed",
                "a changed caption keeps the recipe on its window kind");
        }
        using var pair = new NativeAdapterEnvironment();
        pair.Client.Dialog = true;
        pair.Client.DuplicateWindow = true;
        pair.Client.DuplicateTitle = "*Editor";
        await RefusedAsync(() => PrepareAsync(pair, pair.Write()));
        Require(pair.Client.Mutations.Count == 0, "two main windows without a named window stay ambiguous");
    }

    // A save control disabled in the first observation is recorded as enabled by the earlier write. Preflight accepts it on a clean
    // document, and the save still receives input only after the write has enabled it.
    private static async Task EarlierStepEnablesTargetAsync()
    {
        CompactFunctionDraft draft = await CompileSaveAfterWriteAsync();
        using var reused = new NativeAdapterEnvironment();
        reused.Client.Dialog = true;
        reused.Client.SaveNeedsEdit = true;
        reused.Client.AfterWrite = () => reused.Client.Title = "*Editor";
        var prepared = await PrepareAsync(reused, reused.InstallDraft(draft));
        var result = await reused.Executor.ExecuteAsync(prepared, Parameters("again"), null, null, CancellationToken.None);
        Require(result.Outputs["value"] == "again" && reused.Client.Field == "again", "write then save verifies the recorded output");
        Require(reused.Client.Mutations.SequenceEqual([Tool.TypeText, Tool.ClickElement]), "only the write and the save send input");
    }

    // If the save stays disabled after the write, preflight still passes, but the save's own check refuses input and the write remains.
    private static async Task EarlierStepLeavesTargetDisabledAsync()
    {
        CompactFunctionDraft draft = await CompileSaveAfterWriteAsync();
        using var stuck = new NativeAdapterEnvironment();
        stuck.Client.Dialog = true;
        stuck.Client.SaveNeedsEdit = true;
        stuck.Client.SaveDisabled = true;
        var prepared = await PrepareAsync(stuck, stuck.InstallDraft(draft));
        string message = await FailureMessageAsync(() => stuck.Executor.ExecuteAsync(prepared, Parameters("again"), null, null,
            CancellationToken.None));
        Require(message == "Target is disabled. No input was sent.", "the save reports the typed disabled outcome");
        Require(stuck.Client.Mutations.SequenceEqual([Tool.TypeText]) && stuck.Client.Field == "again",
            "the write completed and the save received no input");
    }

    // A flagged step keeps the strict identity rules: an absent or duplicated save is refused before any input.
    private static async Task AbsentOrAmbiguousLaterTargetAsync()
    {
        CompactFunctionDraft draft = await CompileSaveAfterWriteAsync();
        foreach (string fault in new[] { "absent", "ambiguous" })
        {
            using var test = new NativeAdapterEnvironment();
            test.Client.Dialog = true;
            test.Client.SaveNeedsEdit = true;
            test.Client.SaveAbsent = fault == "absent";
            test.Client.DuplicateSave = fault == "ambiguous";
            var function = test.InstallDraft(draft);
            await RefusedAsync(() => PrepareAsync(test, function));
            Require(test.Client.Mutations.Count == 0, fault + " later target sends no input");
        }
    }

    // A save disabled before the first action with no recorded enabling step keeps the original strict preflight.
    private static async Task LegacyDisabledTargetAsync()
    {
        using var test = new NativeAdapterEnvironment();
        test.Client.Dialog = true;
        test.Client.SaveDisabled = true;
        var save = test.Install("save", [new(CompactRecipeAction.Click, NativeAdapterEnvironment.Selector("Button", "Save", "save"),
            CompactRecipeRoute.Semantic)], [new("shown", NativeAdapterEnvironment.Selector("Edit", "Field", "field"),
            CompactOutputField.Name, CompactOutputCheckKind.Exists)]);
        await RefusedAsync(() => PrepareAsync(test, save));
        Require(test.Client.Mutations.Count == 0, "a legacy disabled target is refused before input");
    }

    // The recorded save window keeps the unsaved marker the editor shows after the write, while the first observation has the plain
    // caption. Compilation must bind that window the same way preflight does, or the save would stay strict.
    private static async Task<CompactFunctionDraft> CompileSaveAfterWriteAsync()
    {
        CompactDemonstration demonstration = await DemonstrateSaveAsync();
        var references = new Dictionary<string, CompactSemanticTarget> { ["field"] = FieldTarget() };
        var request = new CompactCaptureRequest("save-after-write", new(CompactTopicCategory.Use, "Editor"), ["text"],
            [new CompactCaptureOutput("value", "field", CompactOutputField.Value)]);
        Require(CompactCaptureCompiler.TryCompile(demonstration, request, references, out var compiled), "save after write compiles");
        IReadOnlyList<CompactRecipeStep> steps = compiled.Value!.Steps;
        Require(steps[0].EnabledBeforeFirstAction && !steps[1].EnabledBeforeFirstAction,
            "the write records the save as disabled before it");

        return compiled.Value!;
    }

    // Runs the write and then the save on one fixture, observing the state before the first input and after the last one.
    private static async Task<CompactDemonstration> DemonstrateSaveAsync()
    {
        using var recorded = new NativeAdapterEnvironment();
        recorded.Client.Dialog = true;
        recorded.Client.SaveNeedsEdit = true;
        IReadOnlyList<CompactTreeNode> before = (await recorded.Node.ObserveAsync(new(10, null), false, CancellationToken.None)).Nodes;
        var writer = await PrepareAsync(recorded, recorded.Write());
        await recorded.Executor.ExecuteAsync(writer, Parameters("new"), null, null, CancellationToken.None);
        var saver = await PrepareAsync(recorded, recorded.Install("save", [new(CompactRecipeAction.Click,
            NativeAdapterEnvironment.Selector("Button", "Save", "save"), CompactRecipeRoute.Semantic)],
            [new("shown", NativeAdapterEnvironment.Selector("Edit", "Field", "field"), CompactOutputField.Name,
            CompactOutputCheckKind.Exists)]));
        await recorded.Executor.ExecuteAsync(saver, new Dictionary<string, string>(), null, null, CancellationToken.None);
        IReadOnlyList<CompactTreeNode> after = (await recorded.Node.ObserveAsync(new(10, null), false, CancellationToken.None)).Nodes;
        var save = new CompactSemanticTarget(10, "Save", CompactRole.Parse("Button"), "save", CompactWindowKind.Main, []);
        var steps = new List<CompactDemonstrationStep>
        {
            new(CompactRecipeAction.Write, FieldTarget(), CompactRecipeRoute.NativeText, "new"),
            new(CompactRecipeAction.Click, save, CompactRecipeRoute.Semantic)
        };

        return new(Guid.NewGuid(), 10, steps, before, after);
    }

    private static CompactSemanticTarget FieldTarget()
    {
        return new(10, "Field", CompactRole.Parse("Edit"), "field", CompactWindowKind.Main, []);
    }

    private static async Task<string> FailureMessageAsync<T>(Func<Task<T>> action)
    {
        try
        {
            await action();
        }
        catch (CompactNodeException exception)
        {
            return exception.Message;
        }

        throw new InvalidOperationException("Expected native adapter refusal.");
    }

    private static async Task<CompactFunctionPreflight> PrepareAsync(NativeAdapterEnvironment test, CompactLoadedFunction function)
    {
        var instance = (await test.Node.WindowsAsync(CancellationToken.None)).First();

        return await test.Executor.PreflightAsync(function, NativeAdapterEnvironment.Application, instance, null, CancellationToken.None);
    }

    private static IReadOnlyDictionary<string, string> Parameters(string text)
    {
        return new Dictionary<string, string> { ["text"] = text };
    }

    // Two main windows need a named handle: the ambiguous refusal is exact and sends nothing, while the named window binds its kind.
    private static async Task WindowKindChoiceAsync()
    {
        using var pair = new NativeAdapterEnvironment();
        pair.Client.Dialog = true;
        pair.Client.DuplicateWindow = true;
        var write = pair.Write();
        var windows = await pair.Node.WindowsAsync(CancellationToken.None);
        var refusal = await ExceptionMessageAsync(() => pair.Executor.PreflightAsync(write, NativeAdapterEnvironment.Application,
            windows.First(), null, CancellationToken.None));
        Require(refusal == CompactWindowChoice.AmbiguousRefusal && pair.Client.Mutations.Count == 0,
            "two main windows without a named window are refused exactly, with no input");
        var named = windows.Single(window => window.Handle == 101);
        var prepared = await pair.Executor.PreflightAsync(write, NativeAdapterEnvironment.Application, windows.First(), named,
            CancellationToken.None);
        Require(prepared.Windows[CompactWindowKind.Main].Handle == 101, "a named window binds its own window kind");
    }

    // A main step and a dialog step each read the window of their own kind, so every tree names the handle it was read for.
    private static async Task StepKindsReadTheirOwnWindowAsync()
    {
        using var test = new NativeAdapterEnvironment();
        test.Client.Dialog = true;
        test.Client.DuplicateWindow = true;
        test.Client.SecondWindowKind = "dialog";
        var function = test.Install("main-then-dialog",
            [new(CompactRecipeAction.Write, NativeAdapterEnvironment.Selector("Edit", "Field", "field"), CompactRecipeRoute.NativeText,
                "text"),
                new(CompactRecipeAction.Click, NativeAdapterEnvironment.Selector("Button", "Commit", "commit", CompactWindowKind.Dialog),
                    CompactRecipeRoute.Semantic)],
            [new("value", NativeAdapterEnvironment.Selector("Edit", "Field", "field"), CompactOutputField.Value,
                CompactOutputCheckKind.Exists)]);
        var prepared = await PrepareAsync(test, function);
        await test.Executor.ExecuteAsync(prepared, Parameters("dialog"), null, null, CancellationToken.None);
        var handles = test.Client.TreeRequests.Select(request => request["window"]!.GetValue<long>()).ToHashSet();
        Require(handles.Contains(100) && handles.Contains(101), "each step kind reads the handle of its own window");
    }

    private static async Task<string?> ExceptionMessageAsync<T>(Func<Task<T>> action)
    {
        try
        {
            await action();
        }
        catch (CompactNodeException exception)
        {
            return exception.Message;
        }

        return null;
    }

    private static async Task RefusedAsync<T>(Func<Task<T>> action)
    {
        try
        {
            await action();
        }
        catch (CompactNodeException)
        {
            return;
        }

        throw new InvalidOperationException("Expected native adapter refusal.");
    }

    // Named windows reach the node by handle; the handle is remembered for later requests and dropped when Info stops listing it.
    private static async Task WindowSelectionAsync()
    {
        using var test = new NativeAdapterEnvironment();
        test.Client.DuplicateWindow = true;
        test.Client.SecondWindowKind = "dialog";
        var session = WindowSession(test);
        await WindowRowsAsync(session);
        await NamedWindowAsync(test, session);
        await UnknownWindowAsync(test, session);
        await RememberedWindowAsync(test, session);
    }

    // Info names each window by its handle and its kind word, so an agent never has to identify a window by its caption.
    private static async Task WindowRowsAsync(CompactGatewaySession session)
    {
        var info = await session.CallAsync("ui_info", new(), CancellationToken.None);
        var rows = info["windows"]!.AsArray().OfType<JsonObject>().ToArray();
        Require(rows.Any(row => row["window"]!.GetValue<long>() == 101 && row["kind"]!.ToString() == "dialog")
            && rows.Any(row => row["window"]!.GetValue<long>() == 100 && row["kind"]!.ToString() == "main"),
            "ui_info rows carry each window handle and its kind");
    }

    private static async Task NamedWindowAsync(NativeAdapterEnvironment test, CompactGatewaySession session)
    {
        await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10, ["window"] = 101L }, CancellationToken.None);
        Require(LastTreeRequest(test)["window"]!.GetValue<long>() == 101 && LastTreeRequest(test)["pid"]!.GetValue<int>() == 10,
            "a named window reaches get_ui_tree with its process");
        await session.CallAsync("ui_observe", new JsonObject { ["window"] = 101L }, CancellationToken.None);
        Require(LastTreeRequest(test)["pid"]!.GetValue<int>() == 10, "a window alone takes its process from the reply");
    }

    // A handle outside the last ui_info reply, or one that belongs to another process, is refused before any node call.
    private static async Task UnknownWindowAsync(NativeAdapterEnvironment test, CompactGatewaySession session)
    {
        int calls = test.Client.CallCount;
        var unknown = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10, ["window"] = 999L }, CancellationToken.None);
        var mismatch = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 11, ["window"] = 101L }, CancellationToken.None);
        Require(WindowRefused(unknown) && WindowRefused(mismatch), "unknown and mismatching window handles are refused");
        Require(test.Client.CallCount == calls, "a refused window handle makes no node call");
    }

    // A window stays selected for pid-only requests only while Info still lists it under the same process.
    private static async Task RememberedWindowAsync(NativeAdapterEnvironment test, CompactGatewaySession session)
    {
        await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10, ["window"] = 101L }, CancellationToken.None);
        await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10 }, CancellationToken.None);
        Require(LastTreeRequest(test)["window"]?.GetValue<long>() == 101, "a pid-only request keeps the current window");
        test.Client.DuplicateWindow = false;
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10 }, CancellationToken.None);
        Require(LastTreeRequest(test)["window"]?.GetValue<long>() == 100, "a vanished window falls back to the only window of its process");
    }

    // An unknown kind word is a malformed window row, so Info refuses instead of guessing which window receives input.
    private static async Task UnknownWindowKindAsync()
    {
        using var test = new NativeAdapterEnvironment();
        test.Client.WindowKind = "primary";
        var reply = await WindowSession(test).CallAsync("ui_info", new(), CancellationToken.None);
        Require(reply["success"]?.ToString() == "false", "an unknown window kind is refused");
    }

    private static CompactGatewaySession WindowSession(NativeAdapterEnvironment test)
    {
        string library = Path.Combine(test.Root, "window", "library");
        Directory.CreateDirectory(library);

        return new(test.Client, "fixture", new(library, null), () => "2025-11-25");
    }

    private static JsonObject LastTreeRequest(NativeAdapterEnvironment test)
    {
        return test.Client.TreeRequests[^1];
    }

    private static bool WindowRefused(JsonObject reply)
    {
        return reply["success"]?.ToString() == "false"
            && reply["error"]?.ToString() == "The window handle is not one of the current windows; call ui_info again.";
    }

    // Shared parity: each window request answers the same in the compact gateway state as in the JS twin.
    internal static JsonNode WindowSelectionParity(string fixture)
    {
        var corpus = JsonNode.Parse(File.ReadAllText(fixture))!.AsObject();
        var results = new JsonArray();
        foreach (JsonObject testCase in corpus["cases"]!.AsArray().OfType<JsonObject>())
        {
            var state = new CompactGatewayState { Node = "fixture-node" };
            foreach (JsonObject step in testCase["steps"]!.AsArray().OfType<JsonObject>())
            {
                if (step["info"] is JsonArray listed)
                {
                    state.ObservedWindows = listed.OfType<JsonObject>().Select(ParityWindow).ToArray();
                }
                else
                {
                    results.Add(ParitySelection(state, step["arguments"]!.AsObject()));
                }
            }
        }

        return results;
    }

    private static CompactNativeWindow ParityWindow(JsonObject row)
    {
        var kind = row["kind"]!.ToString() == "dialog" ? CompactWindowKind.Dialog : CompactWindowKind.Main;

        return new(row["pid"]!.GetValue<int>(), "fixture-editor", "1.0", "Fixture", row["handle"]!.GetValue<long>(), "start", kind);
    }

    // A refusal of the gateway and of the twin both report only that the selection was not accepted.
    private static JsonObject ParitySelection(CompactGatewayState state, JsonObject arguments)
    {
        try
        {
            var selection = state.SelectWindow(arguments);

            return new JsonObject
            {
                ["ok"] = true,
                ["pid"] = selection.ProcessId,
                ["window"] = selection.Handle is long handle ? JsonValue.Create(handle) : null
            };
        }
        catch (Exception exception) when (exception is CompactNodeException or ArgumentException)
        {
            return new JsonObject { ["ok"] = false };
        }
    }

    // Shared parity: the learning rule proves or refuses each demonstration window the same way in C# and in the JS twin.
    internal static JsonNode WindowInstanceParity(string fixture)
    {
        var corpus = JsonNode.Parse(File.ReadAllText(fixture))!.AsObject();
        var results = new JsonArray();
        foreach (JsonObject testCase in corpus["cases"]!.AsArray().OfType<JsonObject>())
        {
            var windows = testCase["windows"]!.AsArray().OfType<JsonObject>().Select(ParityInstanceWindow).ToArray();
            foreach (JsonObject check in testCase["checks"]!.AsArray().OfType<JsonObject>())
            {
                results.Add(ParityInstance(windows, check));
            }
        }

        return results;
    }

    private static CompactNativeWindow ParityInstanceWindow(JsonObject row)
    {
        var kind = row["kind"]!.ToString() == "dialog" ? CompactWindowKind.Dialog : CompactWindowKind.Main;

        return new(row["pid"]!.GetValue<int>(), "fixture-editor", "1.0", "Fixture", row["handle"]!.GetValue<long>(),
            row["started"]?.GetValue<string>(), kind);
    }

    // A proved window answers with its handle; a refusal answers with its exact message, which the JS twin must repeat.
    private static JsonObject ParityInstance(IReadOnlyList<CompactNativeWindow> windows, JsonObject check)
    {
        long? handle = check["window"] is JsonNode named ? named.GetValue<long>() : null;
        int process = check["pid"]!.GetValue<int>();
        var ofProcess = windows.Where(window => window.ProcessId == process).ToArray();
        try
        {
            var window = CompactGatewayLearning.RequireInstance(ofProcess, handle);

            return new JsonObject { ["ok"] = true, ["handle"] = JsonValue.Create(window.Handle) };
        }
        catch (CompactNodeException exception)
        {
            return new JsonObject { ["ok"] = false, ["error"] = exception.Message };
        }
    }

    private static void Require(bool condition, string name)
    {
        WorkflowEngineSyntheticAdapter.Require(condition, name);
    }
}
