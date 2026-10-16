using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Common;

namespace KLaniBot.Platforms.Mobile;

// The experimental iOS backend: a test runner inside the simulator (XCTest) operates one fixed app and exchanges
// requests and replies with the node through files in a bridge directory on the macOS host. It says nothing about
// the rest of the device: no windows, no clipboard, no background mode and no element identity, so the guarded
// profile cannot authorize input here. The bridge directory must not be writable by other users or contain
// links, because whoever can write a reply there controls what the node believes the app shows.
public sealed class IosSimulatorUiService : IUiService
{
    private readonly string _root;
    private readonly string _bundle;
    private readonly Dictionary<int, VisualTreeNode> _cache = new();
    private int _nextId;
    // Throws PlatformNotSupportedException off macOS, ArgumentException for an invalid bundle id, InvalidDataException before the runner
    // started and UnauthorizedAccessException when the bridge is linked or writable by others.
    public IosSimulatorUiService(string root, string bundle)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The XCTest simulator bridge requires a macOS host.");
        _root = Path.GetFullPath(root);
        _bundle = bundle;
        if (!System.Text.RegularExpressions.Regex.IsMatch(bundle, "^[a-zA-Z][a-zA-Z0-9_.]+$"))
            throw new ArgumentException("A fixed iOS bundle identifier is required.");
        if (!Directory.Exists(_root))
            throw new InvalidDataException("Start the XCTest runner first.");
        for (string? part = _root; part != null; part = Path.GetDirectoryName(part))
            if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Bridge links are forbidden.");
        foreach (string path in new[] { _root, Path.Combine(_root, "requests"), Path.Combine(_root, "replies") })
            if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new UnauthorizedAccessException("Bridge must not be writable by other users.");
        Ready();
    }
    // Throws InvalidDataException when the bridge reports another identity or protocol.
    private void Ready()
    {
        var marker = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "ready.json")))!.AsObject();
        if (marker["protocol"]?.GetValue<int>() != 1
            || marker["ready"]?.GetValue<bool>() != true
            || marker["bundle"]?.ToString() != _bundle)
            throw new InvalidDataException("XCTest bridge identity/protocol mismatch.");
    }
    public string Platform => "ios-simulator-xctest-experimental";
    public IReadOnlySet<ToolFeature> Features
    {
        get;
    } = new HashSet<ToolFeature>
    {
        ToolFeature.SemanticInspect, ToolFeature.PhysicalInput, ToolFeature.TextInjection, ToolFeature.Screenshot
    };
    public IReadOnlySet<Tool>? SupportedTools
    {
        get;
    } = new HashSet<Tool>
    {
        Tool.GetMachineCapabilities, Tool.ListAvailableMcps, Tool.GetActionStatus, Tool.EndSession, Tool.GetUiTree,
        Tool.FindElements, Tool.GetUiDelta, Tool.MouseClick, Tool.TypeText, Tool.TakeScreenshot, Tool.WaitElement,
        Tool.VerifyElement, Tool.FsListDirectory, Tool.FsReadFile, Tool.FsWriteFile, Tool.FsSearchFiles, Tool.FsGetMetadata,
        Tool.ListMacros, Tool.SaveMacro, Tool.RunMacro
    };
    // Throws PlatformNotSupportedException off macOS, TimeoutException without an answer, InvalidDataException for an oversized or
    // unrelated answer and InvalidOperationException when the runner reports failure.
    private JsonObject Call(string action, JsonObject? args = null)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("XCTest bridge requires macOS.");
        Ready();
        string id = Guid.CreateVersion7().ToString();
        var request = args ?? new();
        request["request_id"] = id;
        request["action"] = action;
        string pending = Path.Combine(_root, "requests", id + ".tmp");
        string final = Path.ChangeExtension(pending, ".json");
        string reply = Path.Combine(_root, "replies", id + ".json");
        try
        {
            PrivateFile.WriteAllText(pending, request.ToJsonString());
            File.Move(pending, final);
            var watch = Stopwatch.StartNew();
            while (!File.Exists(reply) && watch.ElapsedMilliseconds < 20000)
                Thread.Sleep(25);
            if (!File.Exists(reply))
                throw new TimeoutException("XCTest response deadline exceeded; action outcome is unknown. Do not retry blindly.");
            if (new FileInfo(reply).Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Bridge response exceeds limit.");
            var result = JsonNode.Parse(File.ReadAllText(reply))!.AsObject();
            if (result["request_id"]?.ToString() != id)
                throw new InvalidDataException("Bridge request correlation mismatch.");
            if (result["success"]?.GetValue<bool>() != true)
                throw new InvalidOperationException(result["message"]?.ToString() ?? "XCTest action failed.");
            return result;
        }
        finally
        {
            if (File.Exists(pending))
                File.Delete(pending);
            // A timed-out mutation stays queued, so late execution cannot be mistaken for cancellation.
            if (File.Exists(reply))
            {
                File.Delete(final);
                File.Delete(reply);
            }
        }
    }
    // Throws NotSupportedException for desktop window filters.
    private VisualTreeNode Observe(int? pid, string? title)
    {
        if (pid != null || title != null)
            throw new NotSupportedException("The runner targets one fixed app; desktop window filters are unsupported.");
        var result = Call("observe");
        _cache.Clear();
        var root = new VisualTreeNode { Role = "Root", Name = _bundle };
        foreach (var item in result["elements"]!.AsArray())
        {
            var node = JsonSerializer.Deserialize<VisualTreeNode>(item!.ToJsonString())!;
            node.Id = checked(++_nextId);
            _cache.Add(node.Id, node);
            root.Children.Add(node);
        }
        return root;
    }
    // Throws ArgumentException when the target has no current unique accessibility identifier.
    private JsonObject Target(int id)
    {
        if (!_cache.TryGetValue(id, out var target) || target.AutoId == "")
            throw new ArgumentException("A current target with a unique accessibility identifier is required.");
        return new()
        {
            ["auto_id"] = target.AutoId,
            ["role"] = target.Role,
            ["name"] = target.Name
        };
    }
    private VisualTreeNode Current(int id)
    {
        var reply = Call("current", Target(id));
        var node = JsonSerializer.Deserialize<VisualTreeNode>(reply["element"]!.ToJsonString())!;
        node.Id = id;
        return node;
    }
    private static string Format(VisualTreeNode root) => string.Join("\n", root.Children.Select(FormatLine));
    private static string FormatLine(VisualTreeNode n)
    {
        string name = JsonSerializer.Serialize(n.Name);
        string autoId = JsonSerializer.Serialize(n.AutoId);
        string value = JsonSerializer.Serialize(n.Value);
        return $"[{n.Id}] {n.Role} {name} (autoId: {autoId}, val: {value}, enabled: {n.IsEnabled}, offscreen: {n.IsOffscreen})";
    }
    public string GetUiTree(WindowSelectionRequest request, int maxDepth = 4)
    {
        if (request.Handle.HasValue)
            return "error: iOS has no desktop windows, so a window handle names nothing here.";
        try
        {
            return Format(Observe(request.ProcessId, request.TitleQuery));
        }
        catch (Exception ex) { return "error: " + ex.Message; }
    }
    public object? GetVisualTree(int? pid, string? title, int maxDepth = 4) => Observe(pid, title);
    public string GetChapters(int? pid, string? title) => "error: Chapter inspection is unsupported by the flat XCTest query.";
    public string InspectSection(string chapterId, int maxDepth = 3) => "error: Unsupported.";
    public ClickOutcome MouseClick(int id, string button = "left", int? x = null, int? y = null)
    {
        if (id <= 0 || button != "left" || x != null || y != null)
            return new(false, "XCTest tap requires an identified element; raw coordinates are unsupported.", null);
        Call("tap", Target(id));
        return new(true, "XCUI tap dispatched.", null);
    }
    public UiOutcome TypeText(int id, string text, bool clear, bool enter, int autoWaitMs)
    {
        if (clear || enter || text.Any(c => char.IsControl(c)))
            return new(false,
                "The experimental XCTest bridge supports literal append only, without control characters. "
                + "Pass clear=false and verify the value.");
        var request = Target(id);
        request["text"] = text;
        request["clear"] = false;
        Call("type", request);
        return new(true, "Literal XCUI input dispatched.");
    }
    public ScreenshotOutcome Screenshot()
    {
        byte[] bytes = Convert.FromBase64String(Call("screenshot")["png"]!.ToString());
        byte[] pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
        bool isBoundedPng = bytes.Length <= 2 * 1024 * 1024 && bytes.AsSpan().StartsWith(pngSignature);
        return isBoundedPng
            ? new(true, "iOS app screenshot captured by XCTest.", bytes)
            : new(false, "Screenshot invalid or too large.", null);
    }
    public ElementCheckOutcome WaitForElement(
        string? name,
        string? autoId = null,
        string? role = null,
        int? pid = null,
        string? title = null,
        string state = "exists",
        int timeoutMs = 5000)
        => VerifyElement(null, name, autoId, role, pid, title, state, null, timeoutMs);
    // Throws ArgumentException for a state this backend cannot check.
    public ElementCheckOutcome VerifyElement(
        int? id,
        string? name,
        string? autoId,
        string? role,
        int? pid,
        string? title,
        string? expectedState,
        string? text,
        int timeoutMs = 5000)
    {
        if (pid != null || title != null || id == null && name == null && autoId == null && role == null)
            return new(false, "A selector in the fixed app is required.", null);
        string state = expectedState ?? "exists";
        var watch = Stopwatch.StartNew();
        do
        {
            VisualTreeNode[] matches = id.HasValue ? [Current(id.Value)] : ObservedMatches(name, autoId, role);
            if (matches.Length > 1)
                return new(false, "Ambiguous selector.", null);
            if (matches.Length == 0 && state == "absent" && text == null)
                return new(true, "Absence verified.", null);
            if (matches.Length == 1 && VerifiedOutcome(matches[0], state, text) is { } verified)
                return verified;
            Thread.Sleep(50);
        } while (watch.ElapsedMilliseconds < timeoutMs);
        return new(false, "Assertion did not match.", null);
    }

    private VisualTreeNode[] ObservedMatches(string? name, string? autoId, string? role)
    {
        Observe(null, null);
        return _cache.Values.Where(n =>
            (name == null || n.Name == name)
            && (autoId == null || n.AutoId == autoId)
            && (role == null || n.Role.Equals(role, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    // The success answer when the one match is in the expected state and shows the expected text, otherwise null.
    // Throws ArgumentException for a state the contract does not name.
    private static ElementCheckOutcome? VerifiedOutcome(VisualTreeNode node, string state, string? text)
    {
        bool match = state switch
        {
            "exists" => true,
            "enabled" => node.IsEnabled,
            "disabled" => !node.IsEnabled,
            "visible" => !node.IsOffscreen,
            "hidden" => node.IsOffscreen,
            "absent" => false,
            _ => throw new ArgumentException("Unsupported state.")
        };

        return match && (text == null || node.Value == text) ? new(true, "State and exact text verified.", node) : null;
    }
    // Throws NotSupportedException always; iOS has no desktop windows.
    public List<WindowInfo> ListWindows() => throw new NotSupportedException("iOS has no desktop windows.");
    public bool FocusWindow(WindowSelectionRequest request) => false;
    public object? GetWindowGeometry(int? pid, string? title) => null;
    public GeometryOutcome SetWindowGeometry(int? pid, string? title, string? state, int? width, int? height, int? x, int? y)
        => new(false, "Unsupported.", null);
    public UiOutcome ClickElement(int id, int autoWaitMs = 3000) => new(false, "XCTest exposes physical tap; use mouse_click.");
    public UiOutcome SilentClick(int id) => new(false, "Unsupported.");
    public UiOutcome SetText(int id, string text, int autoWaitMs = 3000) => new(false, "Reliable replacement is unsupported.");
    public UiOutcome LaunchApp(string path, IReadOnlyList<string> arguments) => new(false, "The runner owns one fixed app.");
    public ClipboardOutcome GetClipboard() => new(false, "Unsupported.", null);
    public UiOutcome SetClipboard(string text) => new(false, "Unsupported.");
    public UiOutcome PasteClipboard(int? id = null) => new(false, "Unsupported.");
    public UiOutcome CloseWindow(int? pid, string? title) => new(false, "Unsupported.");
    public UiOutcome MouseMove(int x, int y) => new(false, "Unsupported.");
    public UiOutcome DragAndDrop(int fromX, int fromY, int toX, int toY) => new(false, "Unsupported.");
    public UiOutcome KeyChord(string[] keys) => new(false, "Unsupported.");
}
