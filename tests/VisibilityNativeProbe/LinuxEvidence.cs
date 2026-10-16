using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace KLaniBot.Visibility;

// A monitor or border window in screen pixels: left, top, width, height.
public sealed record ScreenRectangle(int X, int Y, int Width, int Height);

public sealed partial class LinuxVisibilityHost
{
    private List<MonitorArea>? _testMonitors;

    partial void ReplaceMonitorsInTest(ref List<MonitorArea> monitors)
    {
        if (_testMonitors != null)
        {
            monitors = _testMonitors;
        }
    }

    // Pretends the private display has these monitors (x, y, width, height); the first is the primary one.
    public void TestUseMonitors(ScreenRectangle[] areas)
    {
        Invoke(() =>
        {
            _testMonitors = areas
                .Select((area, index) => new MonitorArea(
                    new Geometry { X = area.X, Y = area.Y, Width = area.Width, Height = area.Height },
                    1,
                    index == 0))
                .ToList();
        });
    }

    // Each monitor must carry its own four edges, and the banner must sit on the primary monitor.
    public void TestBorderOnEveryMonitor(ScreenRectangle[] areas)
    {
        Invoke(() =>
        {
            gdk_display_sync(gdk_display_get_default());
            var placed = _borders
                .Select(window =>
                {
                    gdk_window_get_geometry(gtk_widget_get_window(window), out int x, out int y, out int width, out int height);
                    return new ScreenRectangle(x, y, width, height);
                })
                .ToHashSet();
            if (_borders.Count != 4 * areas.Length)
            {
                throw new Exception("Expected four border windows per monitor, found " + _borders.Count + ".");
            }
            foreach (var area in areas)
            {
                ScreenRectangle[] edges =
                [
                    new(area.X, area.Y, area.Width, 4),
                    new(area.X, area.Y + area.Height - 4, area.Width, 4),
                    new(area.X, area.Y, 4, area.Height),
                    new(area.X + area.Width - 4, area.Y, 4, area.Height)
                ];
                if (!edges.All(placed.Contains))
                {
                    throw new Exception("A monitor at " + area.X + "," + area.Y + " lacks one of its four border edges.");
                }
            }
            gdk_window_get_geometry(gtk_widget_get_window(_assignmentWindow), out int bannerX, out int bannerY, out _, out _);
            var primary = areas[0];
            bool onPrimary = bannerX >= primary.X && bannerX < primary.X + primary.Width
                && bannerY >= primary.Y && bannerY < primary.Y + primary.Height;
            if (!onPrimary)
            {
                throw new Exception("The assignment banner is not on the primary monitor.");
            }
        });
    }

    public int TestAssignmentHeight()
    {
        int height = 0;
        Invoke(() => gdk_window_get_geometry(gtk_widget_get_window(_assignmentWindow), out _, out _, out _, out height));
        return height;
    }

    public void TestAssignment(string directory, string stage, string requiredText)
    {
        Invoke(() => RecordAssignment(directory, stage, requiredText));
    }

    // Runs on the GTK thread.
    private void RecordAssignment(string directory, string stage, string requiredText)
    {
        string text = RequireAssignmentText(requiredText);
        IntPtr native = gtk_widget_get_window(_assignmentWindow);
        if (gdk_window_get_pass_through(native) == 0 || gtk_window_get_accept_focus(_assignmentWindow) != 0
            || gtk_window_get_focus_on_map(_assignmentWindow) != 0 || gtk_widget_get_visible(_assignmentWindow) == 0)
        {
            throw new Exception("The native assignment window must be visible, click-through and non-activating.");
        }
        gdk_window_get_geometry(native, out int x, out int y, out int width, out int height);
        if (width < 100 || height < 60 || y < 0)
        {
            throw new Exception("Assignment window geometry is invalid.");
        }
        string mouse = RequirePointerPassesThrough(native, x + width / 2, y + height / 2);

        CapturePrivateDisplay(Path.Combine(directory, stage + ".png"));
        var record = new JsonObject
        {
            ["diagnostic_simulation"] = true,
            ["private_display"] = Environment.GetEnvironmentVariable("DISPLAY"),
            ["text"] = text,
            ["border_count"] = _borders.Count,
            ["click_through"] = true,
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height,
            ["x11_pointer_hit"] = mouse
        };
        File.WriteAllText(Path.Combine(directory, stage + ".json"), record.ToJsonString());
    }

    private string RequireAssignmentText(string requiredText)
    {
        if (_borders.Count == 0 || _borders.Count % 4 != 0 || _assignmentWindow == IntPtr.Zero)
        {
            throw new Exception("Four border windows per monitor and the assignment window are required.");
        }

        string text = Marshal.PtrToStringUTF8(gtk_label_get_text(_assignmentLabel))!;
        if (!text.Contains(requiredText, StringComparison.Ordinal) || !text.Contains("master-fixture", StringComparison.Ordinal))
        {
            throw new Exception("The native label omitted or retained stale identity/task text.");
        }
        return text;
    }

    // Moves the real X11 pointer onto the banner and asks X which window it hits; it must not be the banner.
    private static string RequirePointerPassesThrough(IntPtr native, int centerX, int centerY)
    {
        gdk_display_sync(gdk_display_get_default());
        RunTool("xdotool", ["mousemove", centerX.ToString(), centerY.ToString()]);
        string mouse = RunTool("xdotool", ["getmouselocation", "--shell"]);

        string assignmentId = gdk_x11_window_get_xid(native).ToString();
        if (mouse.Split('\n').Contains("WINDOW=" + assignmentId))
        {
            throw new Exception("Assignment overlay intercepted the real X11 pointer hit.");
        }
        return mouse;
    }

    public void TestRequirePrivateDisplay()
    {
        Invoke(() =>
        {
            string? openedDisplay = Marshal.PtrToStringUTF8(gdk_display_get_name(gdk_display_get_default()));
            string? privateDisplay = Environment.GetEnvironmentVariable(PrivateGuiSession.PrivateDisplay);
            if (openedDisplay != privateDisplay)
            {
                throw new Exception("GTK opened display '" + openedDisplay + "' instead of the private display '" + privateDisplay + "'.");
            }
        });
    }

    public void TestNoActivityWindows()
    {
        Invoke(() =>
        {
            if (_assignmentWindow != IntPtr.Zero || _assignmentLabel != IntPtr.Zero || _borders.Count != 0)
            {
                throw new Exception("Activity windows survived opt-out or disconnect.");
            }
        });
    }

    private static void CapturePrivateDisplay(string path)
    {
        IntPtr root = gdk_get_default_root_window();
        gdk_window_get_geometry(root, out _, out _, out int width, out int height);
        IntPtr pixels = gdk_pixbuf_get_from_window(root, 0, 0, width, height);
        if (pixels == IntPtr.Zero)
        {
            throw new Exception("Private display capture failed.");
        }
        try
        {
            if (gdk_pixbuf_savev(pixels, path, "png", IntPtr.Zero, IntPtr.Zero, out IntPtr error) == 0)
            {
                if (error != IntPtr.Zero)
                {
                    g_error_free(error);
                }
                throw new Exception("Private display PNG save failed.");
            }
        }
        finally
        {
            g_object_unref(pixels);
        }
    }

    private static string RunTool(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(10000) || process.ExitCode != 0)
        {
            throw new Exception("Private GUI fixture failed: " + executable + " " + error);
        }
        return output;
    }

    [DllImport(Gtk)] private static extern IntPtr gtk_label_get_text(IntPtr label);
    [DllImport(Gtk)] private static extern int gtk_window_get_accept_focus(IntPtr window);
    [DllImport(Gtk)] private static extern int gtk_window_get_focus_on_map(IntPtr window);
    [DllImport(Gdk)] private static extern int gdk_window_get_pass_through(IntPtr window);
    [DllImport(Gdk)] private static extern void gdk_window_get_geometry(IntPtr window, out int x, out int y, out int width, out int height);
    [DllImport(Gdk)] private static extern ulong gdk_x11_window_get_xid(IntPtr window);
    [DllImport(Gdk)] private static extern IntPtr gdk_display_get_name(IntPtr display);
    [DllImport(Gdk)] private static extern IntPtr gdk_get_default_root_window();
    [DllImport(Gdk)] private static extern IntPtr gdk_pixbuf_get_from_window(IntPtr window, int x, int y, int width, int height);
    [DllImport("libgdk_pixbuf-2.0.so.0")]
    private static extern int gdk_pixbuf_savev(IntPtr image, [MarshalAs(UnmanagedType.LPUTF8Str)] string file,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string type, IntPtr keys, IntPtr values, out IntPtr error);
}
