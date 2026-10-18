using System.Diagnostics;
using System.Text.Json;
using KLaniBot.Actions;
using KLaniBot.Guard;

namespace KLaniBot.Platforms.Mirroring;

// Use case: the node operates a phone that a host app mirrors into one window, such as a real iPhone in macOS iPhone Mirroring.
// The phone's content has no accessibility tree, so the observation is the text recognized in that window. Every tap reads the
// window and its text afresh and goes to the centre of the same text where it is now; text that is gone is not tapped. Images
// stay in the node unless take_screenshot asks for one. Coordinates are the only input route into a mirrored phone.
public sealed partial class MirroredPhoneUiService(IMirroredScreen screen, string platform) : IUiService
{
    public const string RecognizedTextRole = "RecognizedText";
    public const string WindowName = "Mirrored phone";

    // A recognized line as the latest observation saw it, and which occurrence of its text it was from the top.
    private sealed record ObservedText(RecognizedLine Line, int Occurrence);

    private readonly Dictionary<int, ObservedText> _cache = new();
    private int _nextId;

    public string Platform => platform;

    // semanticInspect is reported because observation, search and checks run on the recognized text; the elements say so with
    // their role, and no semantic invocation or background input is offered.
    public IReadOnlySet<ToolFeature> Features
    {
        get;
    } = new HashSet<ToolFeature>
    {
        ToolFeature.Windows, ToolFeature.SemanticInspect, ToolFeature.PhysicalInput, ToolFeature.TextInjection, ToolFeature.Screenshot
    };

    public IReadOnlySet<Tool>? SupportedTools
    {
        get;
    } = new HashSet<Tool>
    {
        Tool.GetMachineCapabilities, Tool.ListAvailableMcps, Tool.GetActionStatus, Tool.EndSession, Tool.ListWindows,
        Tool.FocusWindow, Tool.GetWindowGeometry, Tool.GetUiTree, Tool.FindElements, Tool.MouseClick, Tool.Hotkey, Tool.TypeText,
        Tool.TakeScreenshot, Tool.WaitElement, Tool.VerifyElement, Tool.FsListDirectory, Tool.FsReadFile, Tool.FsWriteFile,
        Tool.FsSearchFiles, Tool.FsGetMetadata, Tool.ListMacros, Tool.SaveMacro, Tool.RunMacro
    };

    // Throws NotSupportedException for background interaction, which a mirrored phone does not have.
    public void PrepareElementInteraction(int id, WindowInteractionMode mode, ModalDialogMode dialogs, bool decisionRecorded)
    {
        if (mode != WindowInteractionMode.Foreground)
        {
            throw new NotSupportedException("A mirrored phone takes input only in the foreground.");
        }
    }

    public List<WindowInfo> ListWindows()
    {
        MirroredWindow? window = screen.FindWindow();

        return window == null ? [] : [Describe(window)];
    }

    public bool FocusWindow(WindowSelectionRequest request)
    {
        MirroredWindow? window = screen.FindWindow();
        if (window == null)
        {
            return false;
        }

        return !WindowSelection.HasCriteria(request) || WindowSelection.Select([Describe(window)], request).Outcome
            == WindowSelectionOutcome.Chosen;
    }

    public object? GetWindowGeometry(int? pid, string? titleQuery)
    {
        MirroredWindow? window = screen.FindWindow();

        return window == null ? null : Bounds(window);
    }

    public WindowScreenBounds? ReadWindowBounds(WindowSelectionRequest request)
    {
        MirroredWindow? window = screen.FindWindow();

        return window == null ? null : Bounds(window);
    }

    public string GetUiTree(WindowSelectionRequest request, int maxDepth = 4)
    {
        return JsonSerializer.Serialize(Observe());
    }

    public object? GetVisualTree(int? pid, string? titleQuery, int maxDepth = 4)
    {
        return Observe();
    }

    // The recognized lines as one flat list under the window, each with a fresh observation id.
    // Throws InvalidOperationException when the mirroring window is not visible or its text cannot be read.
    private VisualTreeNode Observe()
    {
        MirroredWindow window = RequireWindow();
        IReadOnlyList<RecognizedLine> lines = screen.Read(window);

        _cache.Clear();
        var root = new VisualTreeNode { Role = "Window", Name = WindowName };
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (RecognizedLine line in lines.OrderBy(line => line.Y).ThenBy(line => line.X))
        {
            int occurrence = seen.TryGetValue(line.Text, out int count) ? count + 1 : 1;
            seen[line.Text] = occurrence;
            int id = checked(++_nextId);
            _cache[id] = new ObservedText(line, occurrence);
            root.Children.Add(Node(id, line, window));
        }
        return root;
    }

    // Throws InvalidOperationException when no mirroring window is visible, for example while the phone is in use.
    private MirroredWindow RequireWindow()
    {
        return screen.FindWindow()
            ?? throw new InvalidOperationException("The mirroring window is not visible; it may be paused or closed. Nothing was sent.");
    }

    private static VisualTreeNode Node(int id, RecognizedLine line, MirroredWindow window)
    {
        ScreenPoint topLeft = window.PointAt(line.X, line.Y);

        return new VisualTreeNode
        {
            Id = id,
            Role = RecognizedTextRole,
            Name = line.Text,
            Value = line.Text,
            IsEnabled = true,
            IsOffscreen = false,
            Bounds = new
            {
                x = (int)Math.Round(topLeft.X),
                y = (int)Math.Round(topLeft.Y),
                width = (int)Math.Round(line.Width * window.Width),
                height = (int)Math.Round(line.Height * window.Height)
            }
        };
    }

    private static WindowInfo Describe(MirroredWindow window)
    {
        return new WindowInfo(window.ProcessId, WindowName, "unknown", WindowName, new IntPtr(window.WindowId));
    }

    private static WindowScreenBounds Bounds(MirroredWindow window)
    {
        return new WindowScreenBounds((int)Math.Round(window.X), (int)Math.Round(window.Y), (int)Math.Round(window.Width),
            (int)Math.Round(window.Height));
    }

    public UiTargetIdentity? GetDisplayedTarget(int id)
    {
        if (!_cache.TryGetValue(id, out ObservedText? observed))
        {
            return null;
        }

        return new UiTargetIdentity(0, "", "", RecognizedTextRole, observed.Line.Text, "", true, true);
    }

    private const int PollMilliseconds = 200;

    // Throws ArgumentException for a state this backend cannot check.
    public ElementCheckOutcome VerifyElement(int? id, string? name, string? autoId, string? role, int? pid, string? windowTitle,
        string? expectedState, string? expectedText, int timeoutMs = 5000)
    {
        string? text = id.HasValue && _cache.TryGetValue(id.Value, out ObservedText? observed) ? observed.Line.Text : name;
        if (text == null || autoId != null)
        {
            return new(false, "A mirrored phone is checked by its visible text; name the text.", null);
        }

        string state = expectedState ?? "exists";
        if (!CheckableStates.Contains(state))
        {
            throw new ArgumentException("A mirrored phone shows text, not the state '" + state + "'.");
        }
        var watch = Stopwatch.StartNew();
        do
        {
            VisualTreeNode root = Observe();
            int count = root.Children.Count(node => node.Name == text);
            if (StateHolds(state, count))
            {
                return new(true, "Visible text verified.", root.Children.FirstOrDefault(node => node.Name == text));
            }
            Thread.Sleep(PollMilliseconds);
        } while (watch.ElapsedMilliseconds < timeoutMs);
        return new(false, "Assertion did not match.", null);
    }

    public ElementCheckOutcome WaitForElement(string? name, string? autoId = null, string? role = null, int? pid = null,
        string? title = null, string state = "exists", int timeoutMs = 5000)
    {
        return VerifyElement(null, name, autoId, role, pid, title, state, null, timeoutMs);
    }

    private static readonly HashSet<string> CheckableStates = new(StringComparer.Ordinal)
    {
        "exists", "visible", "enabled", "absent", "hidden"
    };

    // Recognized text is either there or not; enabled and visible both mean it is shown.
    private static bool StateHolds(string state, int count)
    {
        return state is "absent" or "hidden" ? count == 0 : count > 0;
    }
}
