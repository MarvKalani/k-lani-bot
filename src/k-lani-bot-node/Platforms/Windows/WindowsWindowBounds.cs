#if WINDOWS
using System.Drawing;
using System.Runtime.InteropServices;

namespace KLaniBot.Platforms.Windows;

// Use case: ui_input on Windows reads a window's visible frame in the pixels of the controlled-desktop screenshot. The node runs
// per-monitor DPI aware (V2), so the screenshot, SetCursorPos and the DWM frame all count physical pixels of the virtual screen.
public partial class UiAutomationService
{
    // A rectangle as DwmGetWindowAttribute fills it.
    [StructLayout(LayoutKind.Sequential)]
    private struct FrameRectangle
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // DWMWA_EXTENDED_FRAME_BOUNDS: the frame as drawn, without the invisible resize borders GetWindowRect includes.
    private const uint ExtendedFrameBoundsAttribute = 9;

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetFrameBounds(IntPtr window, uint attribute, out FrameRectangle value, uint size);

    // Null for no matching window, and when the screenshot does not start at the screen origin (a monitor left of or above the
    // primary one): a screenshot pixel is then not the screen point SetCursorPos moves to.
    // Throws ArgumentException for an unknown or ambiguous window, InvalidOperationException for a hidden window or when the
    // monitors or the frame cannot be read.
    public WindowScreenBounds? ReadWindowBounds(WindowSelectionRequest request)
    {
        WindowInfo? window = SelectWindow(ListWindows(), request);
        if (window == null)
        {
            return null;
        }

        Rectangle desktop = WindowsScreenGeometry.ReadPhysicalMonitors().Aggregate(Rectangle.Union);
        if (desktop.Left != 0 || desktop.Top != 0)
        {
            return null;
        }
        FrameRectangle frame = ReadFrame(window.Handle);
        return new WindowScreenBounds(frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
    }

    // Throws InvalidOperationException when DWM cannot report the frame.
    private static FrameRectangle ReadFrame(IntPtr window)
    {
        uint size = (uint)Marshal.SizeOf<FrameRectangle>();

        int result = DwmGetFrameBounds(window, ExtendedFrameBoundsAttribute, out FrameRectangle frame, size);

        if (result != 0 || frame.Right <= frame.Left || frame.Bottom <= frame.Top)
        {
            throw new InvalidOperationException("The window frame could not be read.");
        }
        return frame;
    }
}
#endif
