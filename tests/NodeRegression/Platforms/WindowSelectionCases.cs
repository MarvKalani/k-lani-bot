using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Guard;
using KLaniBot.Platforms;
using KLaniBot.Platforms.Linux;

// Window selection by one shared rule: a native handle names one window, a process alone chooses the dialog that blocks its
// main window, and an accessibility caption matches one window of its own process. Reproduced on Windows 11 with Notepad's
// "Speichern unter" dialog, which made every process-only request ambiguous.
internal static class WindowSelectionCases
{
    private const int ProcessId = 4242;
    private const int OtherProcessId = 4243;
    private const long MainHandle = 0x400007;
    private const long DialogHandle = 0x40004b;
    private const long UnknownHandle = 0x999999;

    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Window selection");
        RunHandleCases(checks);
        RunProcessAndTitleCases(checks);
        RunNamedWindowCases(checks);
        RunWmctrlCases(checks);
        RunRequireChoiceCases(checks);
        RunKindCases(checks);
        RunDispatcherCases(checks, temporaryRoot);
        RunGuardedCases(checks, temporaryRoot);

        return checks.Passed;
    }

    // A guarded program is judged by the process that owns the handle. A handle that no window has is refused, and the window
    // argument authorizes no read beyond get_ui_tree and focus_window.
    private static void RunGuardedCases(Checks checks, string temporaryRoot)
    {
        NodePolicy policy = DispatcherPolicy(temporaryRoot, UiGuardMode.Guarded);
        var ui = new FakeUi();
        var arguments = new JsonObject { ["window"] = DialogHandle };

        checks.That(GuardRefusal(policy, ui, Tool.GetUiTree, arguments) == "Unknown window handle.",
            "a guarded tree read by handle alone refuses a handle that no window has");
        checks.That(GuardRefusal(policy, ui, Tool.GetChapters, arguments)
            == "Guarded observation requires an explicit approved process.",
            "a window argument does not authorize a read that does not take one");
    }

    private static string? GuardRefusal(NodePolicy policy, FakeUi ui, Tool action, JsonObject arguments)
    {
        try
        {
            UiActionGuard.Authorize(policy, policy.Get("operator"), ui, action, arguments);
            return null;
        }
        catch (UnauthorizedAccessException exception)
        {
            return exception.Message;
        }
    }

    // list_windows writes the kind as the word the agent reads, not as a position.
    private static void RunKindCases(Checks checks)
    {
        string dialog = JsonSerializer.Serialize(Window("Save as", DialogHandle, ProcessId, WindowKind.Dialog));
        string main = JsonSerializer.Serialize(Window("Editor", MainHandle, ProcessId, WindowKind.Main));

        checks.That(dialog.Contains("\"Kind\":\"dialog\""), "a dialog is listed with the kind dialog");
        checks.That(main.Contains("\"Kind\":\"main\""), "a main window is listed with the kind main");
    }

    // The dispatcher hands a native handle to the backend unchanged, with the process it is given with.
    private static void RunDispatcherCases(Checks checks, string temporaryRoot)
    {
        var ui = new FakeUi();
        ui.ExtraFeatures.Add(ToolFeature.PhysicalInput);
        var caller = new RequestContext("operator", Guid.CreateVersion7().ToString());
        using var dispatcher = new ActionDispatcher(ui, DispatcherPolicy(temporaryRoot));
        dispatcher.Execute(ToolCall("get_ui_tree", new JsonObject { ["window"] = DialogHandle }), caller);
        checks.That(ui.LastWindowRequest == new WindowSelectionRequest(null, null, DialogHandle),
            "get_ui_tree passes a native handle to the backend");

        var focus = new JsonObject
        {
            ["pid"] = ProcessId,
            ["window"] = DialogHandle,
            ["request_id"] = Guid.CreateVersion7().ToString()
        };
        dispatcher.Execute(ToolCall("focus_window", focus), caller);
        checks.That(ui.LastWindowRequest == new WindowSelectionRequest(ProcessId, null, DialogHandle),
            "focus_window passes the handle together with its process");
    }

    private static JsonObject ToolCall(string action, JsonObject arguments)
    {
        return new JsonObject { ["action"] = action, ["args"] = arguments };
    }

    private static NodePolicy DispatcherPolicy(string temporaryRoot, UiGuardMode mode = UiGuardMode.Desktop)
    {
        string state = Path.Combine(temporaryRoot, "window-handle-state");
        string workspace = Path.Combine(temporaryRoot, "window-handle-workspace");
        Directory.CreateDirectory(state);
        Directory.CreateDirectory(workspace);
        return new NodePolicy
        {
            BackupRequirement = BackupRequirement.Off,
            StateRoot = state,
            WorkspaceRoot = workspace,
            Principals = [new PrincipalPolicy(
                "operator",
                new string('a', 64),
                [Scope.Observe, Scope.Ui],
                [],
                DateTimeOffset.UtcNow.AddHours(1))
            {
                UiGuard = new UiGuardOptions { Mode = mode }
            }]
        };
    }

    private static void RunHandleCases(Checks checks)
    {
        var main = Window("Editor", MainHandle, ProcessId, WindowKind.Main);
        var dialog = Window("Save as", DialogHandle, ProcessId, WindowKind.Dialog);
        var other = Window("Other", 0x500001, OtherProcessId, WindowKind.Main);
        WindowInfo[] windows = [main, dialog, other];

        checks.That(IsChosen(Select(windows, null, null, DialogHandle), dialog), "a handle chooses its own dialog");
        checks.That(IsChosen(Select(windows, null, null, MainHandle), main), "a handle chooses a main window of several");
        checks.That(Select(windows, null, null, UnknownHandle).Outcome == WindowSelectionOutcome.UnknownHandle,
            "a handle that no window has is unknown");
        checks.That(Select(windows, OtherProcessId, null, MainHandle).Outcome == WindowSelectionOutcome.UnknownHandle,
            "a handle with the pid of another process is unknown");
        checks.That(IsChosen(Select(windows, ProcessId, null, DialogHandle), dialog),
            "a handle with its own pid is chosen");
        checks.That(IsChosen(Select(windows, null, "nothing matches", MainHandle), main),
            "a handle ignores the title");
        checks.That(Select([main, Window("Twin", MainHandle, ProcessId, WindowKind.Main)], null, null, MainHandle).Outcome
            == WindowSelectionOutcome.Ambiguous, "two windows with one handle stay ambiguous");
    }

    private static void RunProcessAndTitleCases(Checks checks)
    {
        var main = Window("Editor", MainHandle, ProcessId, WindowKind.Main);
        var secondMain = Window("Editor two", 0x400009, ProcessId, WindowKind.Main);
        var dialog = Window("Save as", DialogHandle, ProcessId, WindowKind.Dialog);
        var secondDialog = Window("Open", 0x40004c, ProcessId, WindowKind.Dialog);

        checks.That(IsChosen(Select([main], ProcessId, null, null), main), "a process with one window is chosen");
        checks.That(Select([main, secondMain], ProcessId, null, null).Outcome == WindowSelectionOutcome.Ambiguous,
            "a process with two main windows is ambiguous");
        checks.That(Select([main, secondMain], ProcessId, null, null).Candidates == 2, "the ambiguous result counts both windows");
        checks.That(IsChosen(Select([main, dialog], ProcessId, null, null), dialog),
            "a process-only request chooses the one dialog among its main window");
        checks.That(IsChosen(Select([dialog, main], ProcessId, null, null), dialog),
            "the order of the windows does not change the dialog choice");
        checks.That(Select([dialog, secondDialog], ProcessId, null, null).Outcome == WindowSelectionOutcome.Ambiguous,
            "two dialogs of one process stay ambiguous");
        checks.That(IsChosen(Select([main, dialog], null, "save", null), dialog), "a title substring still selects a window");
        checks.That(IsChosen(Select([main, dialog], null, "SAVE AS", null), dialog), "a title matches without regard to case");
        checks.That(Select([main, dialog], ProcessId, "absent", null).Outcome == WindowSelectionOutcome.NoMatch,
            "a title that no window shows finds no window");
        checks.That(Select([main, dialog], null, "", null).Outcome == WindowSelectionOutcome.NoMatch,
            "a request with no criteria names no window");
        checks.That(!WindowSelection.HasCriteria(new WindowSelectionRequest(null, "", null)),
            "an empty title is no criterion");
        checks.That(Select([main, secondMain], null, "Editor", null).Outcome == WindowSelectionOutcome.Ambiguous,
            "two main windows that match a title stay ambiguous");
    }

    private static int Single(IReadOnlyList<WindowInfo> windows)
    {
        return WindowSelection.IndexOfSingleUntitledWindow(windows, ProcessId);
    }

    private static void RunNamedWindowCases(Checks checks)
    {
        // Reproduced with Xarchiver: after "New" the process has the main window and "Create a new archive".
        var main = Window("xarchiver 0.5.4.22", 0x400007, ProcessId, WindowKind.Main);
        var dialog = Window("Create a new archive", 0x40004b, ProcessId, WindowKind.Main);
        var decorated = Window("Create a new archive - xarchiver", 0x40004c, ProcessId, WindowKind.Main);

        checks.That(Named([main, dialog], "Create a new archive") == dialog, "the window with the accessible caption is chosen");
        checks.That(Named([dialog, main], "Create a new archive") == dialog, "the order of the windows does not matter");
        checks.That(Named([main, decorated], "Create a new archive") == decorated, "a single decorated caption still matches");
        checks.That(Named([main, dialog, decorated], "Create a new archive") == dialog, "an exact caption wins over a longer one");
        checks.That(Named([main, dialog], "create a NEW archive") == dialog, "a caption matches without regard to case");
        checks.That(Named([main, dialog], null) == null, "no caption leaves the window unnamed");
        checks.That(Named([main, dialog], "") == null, "an empty caption leaves the window unnamed");
        checks.That(Named([main, dialog], "Open an archive") == null, "a caption that no window shows is not guessed");
        checks.That(Named([dialog, Window("Create a new archive", 0x40004d, ProcessId, WindowKind.Main)], "Create a new archive") == null,
            "two windows with the caption stay ambiguous");
        checks.That(Named([Window("Create a new archive", 0x40004e, OtherProcessId, WindowKind.Main)], "Create a new archive") == null,
            "a caption of another process names no window of this process");

        // Reproduced with the Tryton client: its error dialog has no title, and wmctrl prints it without one.
        var untitled = Window("", 0xc003ae, ProcessId, WindowKind.Main);
        var secondUntitled = Window("", 0xc003af, ProcessId, WindowKind.Main);
        var otherUntitled = Window("", 0xc003b0, OtherProcessId, WindowKind.Main);
        checks.That(Named([main, untitled], "") == untitled, "an empty name matches the single untitled window of the process");
        checks.That(Named([main, untitled, secondUntitled], "") == null, "two untitled windows of the process stay ambiguous");
        checks.That(Single([main, untitled]) == 1, "the single untitled window of the process is found by its index");
        checks.That(Single([main, dialog]) == WindowSelection.NoWindow, "a process without an untitled window has none");
        checks.That(Single([untitled, secondUntitled]) == WindowSelection.NoWindow, "two untitled windows have no single one");
        checks.That(Single([otherUntitled, main]) == WindowSelection.NoWindow, "an untitled window of another process is ignored");
        checks.That(Named([main, otherUntitled], "") == null, "an untitled window of another process is not matched");
        checks.That(Named([main, untitled], "xarchiver") == main, "a non-empty name ignores the untitled window");
    }

    // Reproduced in a lab with `wmctrl -l -p -G`: the third line is the untitled error dialog and has eight fields.
    private static void RunWmctrlCases(Checks checks)
    {
        string[] lines =
        [
            "0x0040002b  0 89     0    44   1280 673  9fe4259ef234 Inbox - operator@lab.test - Mozilla Thunderbird",
            "0x00c00007  0 303    0    0    1280 698  9fe4259ef234 Administrator [EUR] - Tryton",
            "0x00c003ae  0 303    341  194  600  400  9fe4259ef234"
        ];
        long[] handles = [0x40002b, 0xc00007, 0xc003ae];
        int[] processIds = [89, 303, 303];
        string[] titles = ["Inbox - operator@lab.test - Mozilla Thunderbird", "Administrator [EUR] - Tryton", ""];
        for (int index = 0; index < lines.Length; index++)
        {
            bool parsed = WmctrlLineParser.TryParse(lines[index], out var window);
            checks.That(parsed && window.Handle == handles[index] && window.ProcessId == processIds[index]
                && window.Title == titles[index], $"wmctrl line {index + 1} gives its handle, process and title");
        }

        bool sevenFields = WmctrlLineParser.TryParse("0x00c003ae  0 303    341  194  600  9fe4259ef234", out _);
        checks.That(!sevenFields, "a line with seven fields is rejected");
        bool badHandle = WmctrlLineParser.TryParse("zz  0 303 341 194 600 400 host Title", out _);
        checks.That(!badHandle, "a handle that is not hexadecimal is rejected");
        checks.That(WmctrlLineParser.TryParse("0x1  0 5 0 0 1 1 host A  B   C", out var spaced) && spaced.Title == "A  B   C",
            "a title with several spaces is kept intact");
    }

    private static void RunRequireChoiceCases(Checks checks)
    {
        var main = Window("Editor", MainHandle, ProcessId, WindowKind.Main);
        var secondMain = Window("Editor two", 0x400009, ProcessId, WindowKind.Main);
        var request = new WindowSelectionRequest(ProcessId, null, null);
        string? processMessage = ThrownMessage(request, WindowSelection.Select([main, secondMain], request));
        var titleRequest = new WindowSelectionRequest(null, "Editor", null);
        string? titleMessage = ThrownMessage(titleRequest, WindowSelection.Select([main, secondMain], titleRequest));
        var handleRequest = new WindowSelectionRequest(null, null, UnknownHandle);
        string? handleMessage = ThrownMessage(handleRequest, WindowSelection.Select([main], handleRequest));

        checks.That(processMessage == "Window selector is ambiguous: process 4242 has 2 matching windows; select one with the "
            + "window argument (the native handle from list_windows).", "a process with two windows names the process and count");
        checks.That(titleMessage == "Window selector is ambiguous: 2 windows match the title; select one with the window argument "
            + "(the native handle from list_windows).", "a title with two windows names the count without a process");
        checks.That(handleMessage == "No window with this native handle exists. Read list_windows again.",
            "an unknown handle says to read list_windows again");
        checks.That(WindowSelection.RequireChoice(request, WindowSelection.Select([main], request)) == main,
            "a chosen window is returned without an error");
    }

    private static WindowSelectionResult Select(WindowInfo[] windows, int? processId, string? title, long? handle)
    {
        return WindowSelection.Select(windows, new WindowSelectionRequest(processId, title, handle));
    }

    private static bool IsChosen(WindowSelectionResult result, WindowInfo expected)
    {
        return result.Outcome == WindowSelectionOutcome.Chosen && result.Window == expected;
    }

    private static WindowInfo? Named(WindowInfo[] windows, string? accessibleName)
    {
        int index = WindowSelection.IndexOfNamedWindow(windows, ProcessId, accessibleName ?? "");

        return index == WindowSelection.NoWindow ? null : windows[index];
    }

    private static string? ThrownMessage(WindowSelectionRequest request, WindowSelectionResult result)
    {
        try
        {
            WindowSelection.RequireChoice(request, result);
            return null;
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }
    }

    private static WindowInfo Window(string title, long handle, int processId, WindowKind kind)
    {
        return new WindowInfo(processId, "app", "1.0", title, new IntPtr(handle)) { Kind = kind };
    }
}
