#if WINDOWS
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using KLaniBot.Actions;
namespace KLaniBot.Platforms.Windows;

public partial class UiAutomationService
{
    public string Platform => "windows-uia";
    public DesktopSessionInfo GetDesktopSession() => WindowsDesktopSession.Read();
    public IReadOnlySet<ToolFeature> Features
    {
        get;
    } = new HashSet<ToolFeature>
    {
        ToolFeature.Windows, ToolFeature.WindowControl, ToolFeature.SemanticInspect, ToolFeature.ApplicationObjects,
        ToolFeature.SemanticInvoke,
        ToolFeature.BackgroundInvoke, ToolFeature.PhysicalInput, ToolFeature.TextInjection, ToolFeature.Screenshot,
        ToolFeature.Clipboard
    };
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out uint value, uint size);

    private static bool? IsWindowCloaked(IntPtr window)
    {
        return DwmGetWindowAttribute(window, 14, out uint value, sizeof(uint)) == 0 ? value != 0 : null;
    }
    // Throws ArgumentException when the request matches several windows or names an unknown handle, and InvalidOperationException
    // when the chosen window is hidden or of unknown visibility.
    private static WindowInfo? SelectWindow(List<WindowInfo> windows, WindowSelectionRequest request)
    {
        if (!WindowSelection.HasCriteria(request))
            return windows.FirstOrDefault(w => w.Handle == NativeMethods.GetForegroundWindow());
        var result = WindowSelection.Select(windows, request);
        var window = WindowSelection.RequireChoice(request, result);
        if (window != null && window.IsCloaked != false)
        {
            throw new InvalidOperationException(
                "The window is hidden on this desktop or its visibility is unknown. It is not selected automatically.");
        }

        return window;
    }

    private static WindowInfo? SelectWindow(List<WindowInfo> windows, int? pid, string? title)
    {
        return SelectWindow(windows, new WindowSelectionRequest(pid, title, null));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint relation);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    // Win32 GW_OWNER: the window that owns this one.
    private const uint OwnerRelation = 4;

    // Win32 GWL_EXSTYLE: the extended window style index.
    private const int ExtendedStyleIndex = -20;

    // Win32 WS_EX_DLGMODALFRAME: a window drawn with a dialog frame.
    private const long DialogFrameStyle = 0x1;

    // An owned window or one with the dialog frame belongs to its program's dialogs; everything else is a main window.
    private static WindowKind ReadWindowKind(IntPtr window)
    {
        bool owned = GetWindow(window, OwnerRelation) != IntPtr.Zero;
        long extendedStyle = GetWindowLongPtr(window, ExtendedStyleIndex).ToInt64();
        bool dialogFrame = (extendedStyle & DialogFrameStyle) != 0;

        return owned || dialogFrame ? WindowKind.Dialog : WindowKind.Main;
    }
    public UiOutcome CloseWindow(int? pid, string? title)
    {
        var window = SelectWindow(ListWindows(), pid, title);
        if (window == null)
            return new(false, "Window not found.");
        return NativeMethods.PostMessage(window.Handle, 0x0010, IntPtr.Zero, IntPtr.Zero)
            ? new(true, "Close requested.")
            : new(false, "Close request failed.");
    }
    public UiOutcome MouseMove(int x, int y)
        => NativeMethods.SetCursorPos(x, y) ? new(true, "Pointer moved.") : new(false, "Pointer movement failed.");
    public UiOutcome DragAndDrop(int fromX, int fromY, int toX, int toY)
    {
        if (!NativeMethods.SetCursorPos(fromX, fromY))
            return new(false, "Invalid starting point.");
        try
        {
            NativeMethods.mouse_event(2, 0, 0, 0, UIntPtr.Zero);
            Thread.Sleep(50);
            if (!NativeMethods.SetCursorPos(toX, toY))
                return new(false, "Invalid end point.");
            return new(true, "Drag dispatched.");
        }
        finally { NativeMethods.mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
    }
    public UiOutcome KeyChord(string[] keys)
    {
        try
        {
            WindowsInput.Chord(keys);
            return new(true, "Key chord dispatched.");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }
    public ScreenshotOutcome Screenshot()
    {
        try
        {
            Rectangle[] monitors = WindowsScreenGeometry.ReadPhysicalMonitors();
            Rectangle bounds = monitors.Aggregate(Rectangle.Union);
            using var bitmap = new Bitmap(bounds.Width, bounds.Height);
            using (var graphics = Graphics.FromImage(bitmap))
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return new(true, "Controlled desktop captured.", stream.ToArray());
        }
        catch (Exception ex) { return new(false, ex.Message, null); }
    }
    public UiOutcome TypeText(int id, string text, bool clear, bool enter, int autoWaitMs)
    {
        if (!_elementCache.TryGetValue(id, out var element))
            return new(false, "Stale element.");
        try
        {
            if (!element.Current.IsEnabled)
                return new(false, "Target disabled.");
            if (clear && element.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && pattern is ValuePattern value)
            {
                if (value.Current.IsReadOnly)
                    return new(false, "Target is read-only.");
                value.SetValue(text);
            }
            else
            {
                element.SetFocus();
                if (!element.GetRuntimeId().SequenceEqual(AutomationElement.FocusedElement.GetRuntimeId()))
                    return new(false, "Focus changed.");
                if (element.Current.ControlType == ControlType.Document)
                {
                    WindowsClipboardPaste.Paste(element, text, clear);
                }
                else
                {
                    if (clear)
                        WindowsInput.Chord(["ctrl", "a"]);
                    WindowsInput.Text(text);
                }
            }
            if (enter)
            {
                element.SetFocus();
                if (!element.GetRuntimeId().SequenceEqual(AutomationElement.FocusedElement.GetRuntimeId()))
                    return new(false, "Focus changed.");
                WindowsInput.Chord(["enter"]);
            }
            return new(true, "Literal text dispatched.");
        }
        catch (Exception ex) { return new(false, ex.Message); }
    }
    private static bool HitMatches(AutomationElement target, int x, int y)
    {
        var hit = AutomationElement.FromPoint(new System.Windows.Point(x, y));
        for (int i = 0; hit != null && i < 32; i++, hit = TreeWalker.ControlViewWalker.GetParent(hit))
            if (hit.GetRuntimeId().SequenceEqual(target.GetRuntimeId()))
                return true;
        return false;
    }
}
// Physical keyboard and mouse input for the Windows backend, sent with SendInput.
internal static class WindowsInput
{
    // A keyboard event as SendInput expects it.
    [StructLayout(LayoutKind.Sequential)]
    private struct Keyboard
    {
        public ushort Key, Scan; public uint Flags, Time; public UIntPtr Extra;
    }
    // A mouse event as SendInput expects it.
    [StructLayout(LayoutKind.Sequential)]
    private struct Mouse
    {
        public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra;
    }
    // The keyboard or mouse part of one input event.
    [StructLayout(LayoutKind.Explicit)]
    private struct Union
    {
        [FieldOffset(0)] public Keyboard Keyboard; [FieldOffset(0)] public Mouse Mouse;
    }
    // One input event as SendInput expects it.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type; public Union Data;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    // Throws InvalidOperationException when Windows rejects the input.
    private static void Send(ushort key, ushort scan, uint flags)
    {
        Input[] input = [new() { Type = 1, Data = new() { Keyboard = new() { Key = key, Scan = scan, Flags = flags } } }];
        if (SendInput(1, input, Marshal.SizeOf<Input>()) != 1)
            throw new InvalidOperationException("Input rejected, possibly by UIPI or a protected desktop.");
    }
    public static void Text(string text)
    {
        foreach (char c in text)
        {
            Send(0, c, 4);
            Send(0, c, 6);
        }
    }
    // Throws ArgumentException for no, too many or unknown keys.
    public static void Chord(string[] keys)
    {
        if (keys.Length is < 1 or > 6)
            throw new ArgumentException("Invalid chord.");
        ushort Key(string key) => key.ToLowerInvariant() switch
        {
            "ctrl" => 0x11,
            "shift" => 0x10,
            "alt" => 0x12,
            "win" => 0x5b,
            "enter" or "return" => 0x0d,
            "tab" => 0x09,
            "escape" => 0x1b,
            "backspace" => 0x08,
            "delete" => 0x2e,
            "left" => 0x25,
            "up" => 0x26,
            "right" => 0x27,
            "down" => 0x28,
            _ when key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) => char.ToUpperInvariant(key[0]),
            _ when key.Length is 2 or 3
                && key.StartsWith("f", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(key[1..], out int f)
                && f is >= 1 and <= 24 => (ushort)(0x70 + f - 1),
            _ => throw new ArgumentException("Unsupported key: " + key)
        };
        var codes = keys.Select(Key).ToArray();
        int pressed = 0;
        try
        {
            foreach (var code in codes)
            {
                Send(code, 0, 0);
                pressed++;
            }
        }
        finally { while (pressed > 0) Send(codes[--pressed], 0, 2); }
    }
}
#endif
