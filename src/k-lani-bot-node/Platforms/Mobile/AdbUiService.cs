using System.Diagnostics;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using KLaniBot.Actions;
using KLaniBot.Common;
using KLaniBot.Guard;

namespace KLaniBot.Platforms.Mobile;

// The Android backend for one device or emulator, through adb and UIAutomator dumps. Input is tapping and
// typing in the foreground; there is no background mode, no clipboard, and no element identity, so the guarded
// profile cannot authorize input here. UIAutomator reports no reliable modal state, so modal dialogs are not
// detected. Terminal commands run in the device's shell, and the application allow-list holds package names.
public sealed class AdbUiService(string executable, string serial) : IUiService
{
    // Throws NotSupportedException always; Android has no background input.
    public void PrepareElementInteraction(int id, WindowInteractionMode mode, ModalDialogMode dialogs, bool decisionRecorded)
    {
        if (mode != WindowInteractionMode.Foreground)
        {
            throw new NotSupportedException("Android has no semantic background input backend.");
        }
        // UIAutomator does not report a reliable modal-window state. The desktop
        // modal pause option must not be advertised as mobile dialog detection.
    }
    // An observed Android element with its identity and the centre a tap goes to.
    private sealed record Element(VisualTreeNode Node, string Identity, string Package, int X, int Y);
    private readonly Dictionary<int, Element> _cache = new();
    private readonly Dictionary<string, int> _chapters = new();
    private int _nextId;
    public string Platform => "android-adb";
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
        Tool.FocusWindow, Tool.GetUiTree, Tool.GetChapters, Tool.InspectSection, Tool.FindElements, Tool.GetUiDelta,
        Tool.MouseClick, Tool.DragAndDrop, Tool.Hotkey, Tool.TypeText, Tool.TakeScreenshot, Tool.WaitElement, Tool.VerifyElement,
        Tool.LaunchProcess, Tool.RunTerminalCommand, Tool.FsListDirectory, Tool.FsReadFile, Tool.FsWriteFile, Tool.FsSearchFiles,
        Tool.FsGetMetadata, Tool.ListMacros, Tool.SaveMacro, Tool.RunMacro
    };
    // Throws InvalidOperationException when adb fails or misses its deadline.
    private string Adb(string[] args, int timeout = 15000)
    {
        var result = ProcessRunner.Run(executable, new[] { "-s", serial }.Concat(args), timeout);
        if (!result.Success)
            throw new InvalidOperationException("ADB rejected the command or exceeded its deadline.");
        return result.Output.TrimEnd();
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    private string Shell(params string[] args) => Adb(["shell", string.Join(" ", args.Select(Quote))]);
    // Throws ArgumentException for an empty command.
    public object RunTerminal(string command, int timeout)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("Command is required.");
        return new
        {
            success = true,
            stdout = Adb(["shell", command], timeout)
        };
    }
    // Throws InvalidDataException when the hierarchy is unavailable or too large.
    private List<Element> ReadHierarchy()
    {
        string path = "/data/local/tmp/k-lani-bot-" + Guid.CreateVersion7() + ".xml";
        try
        {
            Shell("uiautomator", "dump", path);
            Shell("chmod", "600", path);
            XDocument document = ParseHierarchy(Shell("cat", path));
            var elements = new List<Element>();
            Visit(document.Root!, "", elements);
            return elements;
        }
        finally { try { Shell("rm", "-f", path); } catch (InvalidOperationException) { } }
    }

    private const int MaximumHierarchyCharacters = 4 * 1024 * 1024;
    private const int MaximumElements = 2000;

    // The dump comes from the device, so it is parsed with DTDs and external resolution off and a size cap.
    // Throws InvalidDataException when the dump is no UI hierarchy.
    private static XDocument ParseHierarchy(string xml)
    {
        var readerSettings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumHierarchyCharacters
        };
        using var reader = XmlReader.Create(new StringReader(xml), readerSettings);
        var document = XDocument.Load(reader);

        if (document.Root?.Name != "hierarchy")
            throw new InvalidDataException("UI hierarchy unavailable; absence cannot be inferred.");
        return document;
    }

    // The node path (child indexes from the root) is part of the identity, so two equal controls stay distinct.
    // Throws InvalidDataException when the hierarchy has more elements than the limit.
    private static void Visit(XElement item, string nodePath, List<Element> elements)
    {
        if (elements.Count >= MaximumElements)
            throw new InvalidDataException("Hierarchy too large; narrow the app view.");

        if (ElementOf(item, nodePath) is { } element)
            elements.Add(element);
        int index = 0;
        foreach (var child in item.Elements())
            Visit(child, nodePath + "/" + index++, elements);
    }

    // Null for an item without parseable bounds.
    private static Element? ElementOf(XElement item, string nodePath)
    {
        string Attribute(string key) => item.Attribute(key)?.Value ?? "";
        var bounds = System.Text.RegularExpressions.Regex.Match(Attribute("bounds"), @"^\[(-?\d+),(-?\d+)\]\[(-?\d+),(-?\d+)\]$");
        if (!bounds.Success)
            return null;

        int x1 = int.Parse(bounds.Groups[1].Value);
        int y1 = int.Parse(bounds.Groups[2].Value);
        int x2 = int.Parse(bounds.Groups[3].Value);
        int y2 = int.Parse(bounds.Groups[4].Value);
        string name = Attribute("text");
        if (name == "")
            name = Attribute("content-desc");
        string role = Attribute("class").Split('.').Last();
        var node = new VisualTreeNode
        {
            Name = name,
            Value = Attribute("text"),
            AutoId = Attribute("resource-id"),
            Role = role,
            IsEnabled = Attribute("enabled") == "true",
            IsOffscreen = x2 <= x1 || y2 <= y1,
            Bounds = new { x = x1, y = y1, width = x2 - x1, height = y2 - y1 }
        };
        string identity = JsonSerializer.Serialize(new[] { node.AutoId, node.Role, node.Name, nodePath, Attribute("package") });
        return new Element(node, identity, Attribute("package"), x1 + (x2 - x1) / 2, y1 + (y2 - y1) / 2);
    }
    private VisualTreeNode Observe(int maxDepth)
    {
        var elements = ReadHierarchy();
        _cache.Clear();
        var root = new VisualTreeNode { Role = "Root", Name = "Android accessibility hierarchy" };
        foreach (var element in elements)
        {
            element.Node.Id = checked(++_nextId);
            _cache[element.Node.Id] = element;
            root.Children.Add(element.Node);
        }
        return root;
    }
    // Throws ArgumentException when the target is from an older observation, vanished or became ambiguous.
    private Element Current(int id)
    {
        if (!_cache.TryGetValue(id, out var previous))
            throw new ArgumentException("Stale target.");
        var matches = ReadHierarchy().Where(e => e.Identity == previous.Identity).ToArray();
        if (matches.Length != 1)
            throw new ArgumentException("Target vanished, changed identity or became ambiguous.");
        return matches[0];
    }
    // Throws InvalidOperationException when the focused activity cannot be read.
    public List<WindowInfo> ListWindows()
    {
        string dump = Shell("dumpsys", "window", "windows");
        var match = System.Text.RegularExpressions.Regex.Match(dump, @"mCurrentFocus=Window\{[^\s]+\s+\S+\s+([^\s}]+)");
        if (!match.Success)
            throw new InvalidOperationException("Focused activity unavailable.");
        string title = match.Groups[1].Value, package = title.Split('/')[0];
        string pids = Shell("pidof", package);
        int pid = int.TryParse(pids.Split(' ')[0], out int value) ? value : 0;
        return [new WindowInfo(pid, package, "unknown", title, IntPtr.Zero)];
    }
    // The focused activity is the only window, so a request without criteria accepts it.
    public bool FocusWindow(WindowSelectionRequest request)
    {
        var windows = ListWindows();
        if (!WindowSelection.HasCriteria(request))
            return windows.Count > 0;

        return WindowSelection.Select(windows, request).Outcome == WindowSelectionOutcome.Chosen;
    }
    public object? GetWindowGeometry(int? pid, string? title) => null;
    public GeometryOutcome SetWindowGeometry(int? pid, string? title, string? state, int? width, int? height, int? x, int? y)
        => new(false, "Desktop window geometry is unsupported on Android.", null);
    public string GetUiTree(WindowSelectionRequest request, int maxDepth = 4)
    {
        if (request.ProcessId.HasValue || request.TitleQuery != null || request.Handle.HasValue)
            return "error: Android hierarchy targets the current device view; desktop window filters are unsupported.";
        return JsonSerializer.Serialize(Observe(maxDepth));
    }
    // Throws NotSupportedException for desktop window filters.
    public object? GetVisualTree(int? pid, string? title, int maxDepth = 4)
    {
        if (pid.HasValue || title != null)
            throw new NotSupportedException("Desktop filters are unsupported on Android.");
        return Observe(maxDepth);
    }
    public string GetChapters(int? pid, string? title)
    {
        var root = (VisualTreeNode)GetVisualTree(pid, title, 2)!;
        _chapters.Clear();
        foreach (var node in root.Children)
            _chapters["K" + node.Id] = node.Id;
        return string.Join("\n", root.Children.Select(n => $"[K{n.Id}] {n.Role} {JsonSerializer.Serialize(n.Name)}"));
    }
    public string InspectSection(string chapter, int maxDepth = 3)
        => _chapters.TryGetValue(chapter, out int id) ? JsonSerializer.Serialize(Current(id).Node) : "error: Stale section.";
    public UiOutcome ClickElement(int id, int autoWaitMs = 3000)
        => new(false, "Android ADB exposes physical touch, not semantic invocation.");
    public UiOutcome SilentClick(int id) => ClickElement(id);
    public ClickOutcome MouseClick(int id, string button = "left", int? x = null, int? y = null)
    {
        if (button != "left")
            return new(false, "Unsupported Android mouse button.", null);
        if (id > 0)
        {
            var target = Current(id);
            if (!target.Node.IsEnabled || target.Node.IsOffscreen)
                return new(false, "Target is not actionable.", null);
            x = target.X;
            y = target.Y;
        }
        if (!x.HasValue || !y.HasValue)
            return new(false, "Target or coordinates required.", null);
        Shell("input", "tap", x.Value.ToString(), y.Value.ToString());
        return new(true, "Touch dispatched.", new
        {
            x,
            y
        });
    }
    public UiOutcome TypeText(int id, string text, bool clear, bool enter, int wait)
    {
        if (clear || text.Any(c => c < 32 || c > 126) || text.Contains("%s", StringComparison.Ordinal))
            return new(false,
                "ADB text injection supports explicit printable-ASCII append only. "
                + "Replacement and Unicode need a trusted device text bridge; text is never silently corrupted.");
        var target = Current(id);
        if (!target.Node.IsEnabled || target.Node.IsOffscreen)
            return new(false, "Target is not actionable.");
        Shell("input", "tap", target.X.ToString(), target.Y.ToString());
        Shell("input", "text", text.Replace(" ", "%s"));
        if (enter)
            Shell("input", "keyevent", "66");
        return new(true, "Literal ASCII append dispatched.");
    }
    public UiOutcome SetText(int id, string text, int wait = 3000) => TypeText(id, text, true, false, wait);
    public UiOutcome KeyChord(string[] keys)
    {
        var mapping = new Dictionary<string, string>
        {
            ["enter"] = "66",
            ["return"] = "66",
            ["back"] = "4",
            ["home"] = "3",
            ["tab"] = "61",
            ["escape"] = "111",
            ["backspace"] = "67",
            ["left"] = "21",
            ["right"] = "22",
            ["up"] = "19",
            ["down"] = "20"
        };
        if (keys.Length != 1 || !mapping.TryGetValue(keys[0].ToLowerInvariant(), out string? code))
            return new(false, "Unsupported Android key or chord.");
        Shell("input", "keyevent", code);
        return new(true, "Key dispatched.");
    }
    public UiOutcome LaunchApp(string package, IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 0)
            return new(false, "Android package launch arguments are unsupported.");
        Shell("monkey", "-p", package, "-c", "android.intent.category.LAUNCHER", "1");
        return new(true, "Package launch requested.");
    }
    public UiOutcome CloseWindow(int? pid, string? title) => new(false, "Unsupported.");
    public UiOutcome MouseMove(int x, int y) => new(false, "Android has no desktop pointer.");
    public UiOutcome DragAndDrop(int fromX, int fromY, int toX, int toY)
    {
        Shell("input", "swipe", fromX.ToString(), fromY.ToString(), toX.ToString(), toY.ToString(), "500");
        return new(true, "Swipe dispatched.");
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
        string? state,
        string? text,
        int timeoutMs = 5000)
    {
        if (pid.HasValue || title != null || id == null && name == null && autoId == null && role == null)
            return new(false, "An Android selector is required; desktop filters are unsupported.", null);
        string expected = state ?? "exists";
        var watch = Stopwatch.StartNew();
        do
        {
            Element[] matches = id.HasValue ? [Current(id.Value)] : ObservedMatches(name, autoId, role);
            if (matches.Length > 1)
                return new(false, "Ambiguous selector.", null);
            if (matches.Length == 0 && expected == "absent")
                return new(true, "Absence verified.", null);
            if (matches.Length == 1 && VerifiedOutcome(matches[0].Node, expected, text) is { } verified)
                return verified;
            Thread.Sleep(50);
        } while (watch.ElapsedMilliseconds < timeoutMs);
        return new(false, "Assertion did not match.", null);
    }

    private Element[] ObservedMatches(string? name, string? autoId, string? role)
    {
        Observe(12);
        return _cache.Values.Where(e =>
            (name == null || e.Node.Name == name)
            && (autoId == null || e.Node.AutoId == autoId)
            && (role == null || e.Node.Role.Equals(role, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    // The success answer when the one match is in the expected state and shows the expected text, otherwise null.
    // Throws ArgumentException for a state the contract does not name.
    private static ElementCheckOutcome? VerifiedOutcome(VisualTreeNode node, string expected, string? text)
    {
        bool matched = expected switch
        {
            "exists" => true,
            "enabled" => node.IsEnabled,
            "disabled" => !node.IsEnabled,
            "visible" => !node.IsOffscreen,
            "hidden" => node.IsOffscreen,
            "absent" => false,
            _ => throw new ArgumentException("Unsupported state.")
        };

        return matched && (text == null || node.Value == text) ? new(true, "State and exact text verified.", node) : null;
    }
    public ClipboardOutcome GetClipboard() => new(false, "Unsupported.", null);
    public UiOutcome SetClipboard(string text) => new(false, "Unsupported.");
    public UiOutcome PasteClipboard(int? id = null) => new(false, "Unsupported.");
    // Throws InvalidDataException when the image exceeds the transfer limit.
    public ScreenshotOutcome Screenshot()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = new Process { StartInfo = startInfo };
        foreach (var arg in new[] { "-s", serial, "exec-out", "screencap", "-p" })
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        // Not inherited: see ProcessRunner for why a child must not share the node's standard input.
        process.StandardInput.Close();
        using var deadline = new CancellationTokenSource(15000);
        var error = process.StandardError.ReadToEndAsync(deadline.Token);
        try
        {
            byte[] png = ReadTransfer(process, error, deadline.Token);
            byte[] pngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
            bool isPng = process.ExitCode == 0 && png.AsSpan().StartsWith(pngSignature);
            return isPng ? new(true, "Device screenshot captured.", png) : new(false, "ADB screenshot failed.", null);
        }
        catch (Exception exception)
        {
            // Broad on purpose: whatever stopped the transfer, adb must not keep running in the background.
            process.Kill(true);
            process.WaitForExit();
            return new(false, "ADB screenshot unavailable (" + exception.GetType().Name + ").", null);
        }
    }

    private const int MaximumScreenshotBytes = 2 * 1024 * 1024;

    // Throws InvalidDataException when the device sends more than the transfer limit.
    private static byte[] ReadTransfer(Process process, Task<string> error, CancellationToken cancellation)
    {
        using var data = new MemoryStream();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = process.StandardOutput.BaseStream.ReadAsync(buffer, cancellation).AsTask().GetAwaiter().GetResult()) > 0)
        {
            if (data.Length + count > MaximumScreenshotBytes)
                throw new InvalidDataException("Screenshot exceeds transfer limit.");
            data.Write(buffer, 0, count);
        }

        process.WaitForExitAsync(cancellation).GetAwaiter().GetResult();
        error.GetAwaiter().GetResult();
        return data.ToArray();
    }
}
