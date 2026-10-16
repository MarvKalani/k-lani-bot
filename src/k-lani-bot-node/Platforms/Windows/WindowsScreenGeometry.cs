#if WINDOWS
using System.Drawing;
using System.Runtime.InteropServices;

namespace KLaniBot.Platforms.Windows;

// The Windows indicator reads the monitors here in physical pixels, so the border covers every screen at any scaling.
internal static class WindowsScreenGeometry
{
    // A rectangle as GetMonitorInfo fills it.
    [StructLayout(LayoutKind.Sequential)]
    private struct RectangleNative
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // A monitor's area and flags as GetMonitorInfo fills them.
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInformation
    {
        public int Size;
        public RectangleNative Monitor;
        public RectangleNative WorkArea;
        public uint Flags;
    }

    // Called by EnumDisplayMonitors for each monitor.
    private delegate bool MonitorCallback(IntPtr monitor, IntPtr device, IntPtr rectangle, IntPtr state);
    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr device, IntPtr clip, MonitorCallback callback, IntPtr state);
    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInformation information);
    [DllImport("user32.dll")]
    public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    // Throws InvalidOperationException when the monitor coordinates cannot be read.
    public static Rectangle[] ReadPhysicalMonitors()
    {
        var rectangles = new List<Rectangle>();
        IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try
        {
            MonitorCallback collect = CollectInto(rectangles);
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero) || rectangles.Count == 0)
            {
                throw new InvalidOperationException("Physical monitor coordinates unavailable.");
            }
            return rectangles.ToArray();
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                SetThreadDpiAwarenessContext(previous);
            }
        }
    }

    // The primary monitor goes first, because the assignment banner is placed on the first one.
    private static MonitorCallback CollectInto(List<Rectangle> rectangles)
    {
        return (monitor, _, _, _) =>
        {
            var information = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>() };
            if (!GetMonitorInfo(monitor, ref information))
            {
                return false;
            }
            var rectangle = Rectangle.FromLTRB(
                information.Monitor.Left,
                information.Monitor.Top,
                information.Monitor.Right,
                information.Monitor.Bottom);
            if ((information.Flags & 1) != 0)
            {
                rectangles.Insert(0, rectangle);
            }
            else
            {
                rectangles.Add(rectangle);
            }
            return true;
        };
    }
}
#endif
