#if !WINDOWS
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using KLaniBot.Common;
using KLaniBot.Localization;
using KLaniBot.Visibility;

namespace KLaniBot.ControllerTray;

// The Linux controller tray: a StatusNotifierItem through libayatana-appindicator3 with a GTK 3 menu. KDE Plasma shows it
// natively and GNOME with its AppIndicator extension, under Wayland and X11 alike; GtkStatusIcon (the node's tray) needs
// XEmbed and stays invisible on a Wayland desktop. The indicator takes icons by name, so the four state icons are written
// as PNG files into a private runtime directory.
internal sealed class LinuxControllerTray : ControllerTrayHost
{
    private const string Gtk = "libgtk-3.so.0";
    private const string Obj = "libgobject-2.0.so.0";
    private const string Glib = "libglib-2.0.so.0";
    private const string Pixbuf = "libgdk_pixbuf-2.0.so.0";
    private const string Indicator = "libayatana-appindicator3.so.1";
    private const int ApplicationStatusCategory = 0;
    private const int ActiveIndicatorStatus = 1;
    private const uint DrainIntervalMilliseconds = 200;
    private const int GenerationShift = 16;
    private const int IndexMask = 0xFFFF;

    private readonly IntPtr _indicator;
    private readonly string _iconDirectory;
    private readonly ConcurrentQueue<ControllerTrayView> _views = new();
    private readonly SourceCallback _drain;
    private readonly ActivateCallback _activate;
    private List<ControllerTrayCommand> _commands = new();
    private int _generation;
    private volatile bool _closing;

    // The GLib timeout handler that drains views and the close request on the GTK thread.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SourceCallback(IntPtr data);

    // The GTK handler of a menu item; data carries the menu generation and the command's index.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ActivateCallback(IntPtr widget, IntPtr data);

    // Throws InvalidOperationException without a GTK desktop or without libayatana-appindicator3.
    public LinuxControllerTray()
    {
        if (gtk_init_check(IntPtr.Zero, IntPtr.Zero) == 0)
        {
            throw new InvalidOperationException("The controller tray needs a graphical desktop session.");
        }
        _iconDirectory = CreateIconDirectory();
        WriteStateIcons(_iconDirectory);
        _indicator = CreateIndicator(_iconDirectory);
        _drain = Drain;
        _activate = Activate;
        g_timeout_add(DrainIntervalMilliseconds, Marshal.GetFunctionPointerForDelegate(_drain), IntPtr.Zero);
    }

    // Throws InvalidOperationException when the indicator library is not installed.
    private static IntPtr CreateIndicator(string iconDirectory)
    {
        try
        {
            // A D-Bus object path is derived from the identifier, which therefore must not contain hyphens.
            string firstIcon = IconName(ControllerTrayIcon.MasterMissing);
            IntPtr indicator = app_indicator_new("k_lani_bot_controller", firstIcon, ApplicationStatusCategory);
            app_indicator_set_icon_theme_path(indicator, iconDirectory);
            app_indicator_set_title(indicator, UiTexts.Get(UiText.ControllerTrayTitle));
            app_indicator_set_status(indicator, ActiveIndicatorStatus);
            return indicator;
        }
        catch (DllNotFoundException)
        {
            throw new InvalidOperationException("The controller tray needs libayatana-appindicator3 (package libayatana-appindicator3-1).");
        }
    }

    private static string CreateIconDirectory()
    {
        string? runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string runtime = string.IsNullOrEmpty(runtimeDirectory) ? Path.GetTempPath() : runtimeDirectory;
        string icons = Path.Combine(runtime, "k-lani-bot-controller-tray-" + Environment.ProcessId);
        PrivateFile.CreateDirectory(icons);
        return icons;
    }

    private static string IconName(ControllerTrayIcon icon)
    {
        return "k-lani-bot-controller-" + icon.ToString().ToLowerInvariant();
    }

    private static uint BadgeColor(ControllerTrayIcon icon)
    {
        return icon switch
        {
            ControllerTrayIcon.On => 0x379eefff,
            ControllerTrayIcon.Working => 0xff3939ff,
            ControllerTrayIcon.Off => 0x888888ff,
            ControllerTrayIcon.MasterMissing => 0xe8a33dff,
            _ => throw new UnreachableException()
        };
    }

    // Throws InvalidDataException when the product icon cannot be decoded or a state icon cannot be written.
    private static void WriteStateIcons(string directory)
    {
        foreach (ControllerTrayIcon icon in Enum.GetValues<ControllerTrayIcon>())
        {
            IntPtr image = LoadProductIcon();
            try
            {
                PaintBadge(image, BadgeColor(icon));
                string path = Path.Combine(directory, IconName(icon) + ".png");
                if (gdk_pixbuf_savev(image, path, "png", IntPtr.Zero, IntPtr.Zero, out IntPtr error) == 0)
                {
                    FreeError(error);
                    throw new InvalidDataException("The controller tray icon could not be written.");
                }
            }
            finally
            {
                g_object_unref(image);
            }
        }
    }

    // Throws InvalidDataException when the image cannot be decoded.
    private static IntPtr LoadProductIcon()
    {
        IntPtr loader = gdk_pixbuf_loader_new();
        try
        {
            byte[] bytes = ProductBranding.Read(ProductImage.Icon32);
            if (gdk_pixbuf_loader_write(loader, bytes, (nuint)bytes.Length, out IntPtr error) == 0
                || gdk_pixbuf_loader_close(loader, out error) == 0)
            {
                FreeError(error);
                throw new InvalidDataException("The product icon could not be decoded.");
            }
            return gdk_pixbuf_copy(gdk_pixbuf_loader_get_pixbuf(loader));
        }
        finally
        {
            g_object_unref(loader);
        }
    }

    private static void FreeError(IntPtr error)
    {
        if (error != IntPtr.Zero)
        {
            g_error_free(error);
        }
    }

    // A filled square in the lower right third, the same place as the node's status badge.
    private static void PaintBadge(IntPtr image, uint rgba)
    {
        int width = gdk_pixbuf_get_width(image);
        int height = gdk_pixbuf_get_height(image);
        int rowStride = gdk_pixbuf_get_rowstride(image);
        int channels = gdk_pixbuf_get_n_channels(image);
        IntPtr pixels = gdk_pixbuf_get_pixels(image);
        for (int y = height * 2 / 3; y < height; y++)
        {
            for (int x = width * 2 / 3; x < width; x++)
            {
                int offset = y * rowStride + x * channels;
                Marshal.WriteByte(pixels, offset, (byte)(rgba >> 24));
                Marshal.WriteByte(pixels, offset + 1, (byte)(rgba >> 16));
                Marshal.WriteByte(pixels, offset + 2, (byte)(rgba >> 8));
                if (channels == 4)
                {
                    Marshal.WriteByte(pixels, offset + 3, byte.MaxValue);
                }
            }
        }
    }

    public override void Show(ControllerTrayView view)
    {
        _views.Enqueue(view);
    }

    public override void Run()
    {
        gtk_main();
    }

    public override void Close()
    {
        _closing = true;
    }

    // Runs on the GTK thread; only the newest waiting view is drawn.
    private int Drain(IntPtr data)
    {
        if (_closing)
        {
            gtk_main_quit();
            return 0;
        }
        ControllerTrayView? newest = null;
        while (_views.TryDequeue(out ControllerTrayView? view))
        {
            newest = view;
        }
        if (newest != null)
        {
            Draw(newest);
        }
        return 1;
    }

    private void Draw(ControllerTrayView view)
    {
        app_indicator_set_icon_full(_indicator, IconName(view.Icon), view.Tooltip);
        app_indicator_set_title(_indicator, view.Tooltip);
        _generation = (_generation + 1) & IndexMask;
        var commands = new List<ControllerTrayCommand>();
        IntPtr menu = gtk_menu_new();
        foreach (ControllerTrayEntry entry in view.Entries)
        {
            gtk_menu_shell_append(menu, CreateItem(entry, commands));
        }
        gtk_widget_show_all(menu);

        _commands = commands;
        app_indicator_set_menu(_indicator, menu);
    }

    private IntPtr CreateItem(ControllerTrayEntry entry, List<ControllerTrayCommand> commands)
    {
        if (entry.Kind == ControllerTrayEntryKind.Separator)
        {
            return gtk_separator_menu_item_new();
        }
        string text = entry.Indented ? "      " + entry.Text : entry.Text;
        IntPtr item = gtk_menu_item_new_with_label(text);
        if (entry.Command == null)
        {
            gtk_widget_set_sensitive(item, 0);
            return item;
        }

        commands.Add(entry.Command);
        IntPtr data = (IntPtr)((_generation << GenerationShift) | (commands.Count - 1));
        g_signal_connect_data(item, "activate", Marshal.GetFunctionPointerForDelegate(_activate), data, IntPtr.Zero, 0);
        return item;
    }

    // A click on an item of a menu that was replaced meanwhile is ignored rather than mapped onto the new menu's lines.
    private void Activate(IntPtr widget, IntPtr data)
    {
        int value = (int)data;
        int generation = value >> GenerationShift;
        int index = value & IndexMask;
        if (generation == _generation && index < _commands.Count)
        {
            Choose(_commands[index]);
        }
    }

    public override void Dispose()
    {
        try
        {
            Directory.Delete(_iconDirectory, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The runtime directory is private and cleared at logout; a leftover icon file does no harm.
        }
    }

    [DllImport(Gtk)] private static extern int gtk_init_check(IntPtr argc, IntPtr argv);
    [DllImport(Gtk)] private static extern void gtk_main();
    [DllImport(Gtk)] private static extern void gtk_main_quit();
    [DllImport(Gtk)] private static extern IntPtr gtk_menu_new();
    [DllImport(Gtk)] private static extern IntPtr gtk_menu_item_new_with_label([MarshalAs(UnmanagedType.LPUTF8Str)] string label);
    [DllImport(Gtk)] private static extern IntPtr gtk_separator_menu_item_new();
    [DllImport(Gtk)] private static extern void gtk_menu_shell_append(IntPtr menu, IntPtr item);
    [DllImport(Gtk)] private static extern void gtk_widget_set_sensitive(IntPtr widget, int sensitive);
    [DllImport(Gtk)] private static extern void gtk_widget_show_all(IntPtr widget);
    [DllImport(Obj)] private static extern void g_object_unref(IntPtr instance);
    [DllImport(Obj)]
    private static extern ulong g_signal_connect_data(IntPtr instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string signal,
        IntPtr handler, IntPtr data, IntPtr destroy, int flags);
    [DllImport(Glib)] private static extern uint g_timeout_add(uint interval, IntPtr function, IntPtr data);
    [DllImport(Glib)] private static extern void g_error_free(IntPtr error);
    [DllImport(Pixbuf)] private static extern IntPtr gdk_pixbuf_loader_new();
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_loader_write(IntPtr loader, byte[] buffer, nuint count, out IntPtr error);
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_loader_close(IntPtr loader, out IntPtr error);
    [DllImport(Pixbuf)] private static extern IntPtr gdk_pixbuf_loader_get_pixbuf(IntPtr loader);
    [DllImport(Pixbuf)] private static extern IntPtr gdk_pixbuf_copy(IntPtr pixbuf);
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_get_width(IntPtr pixbuf);
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_get_height(IntPtr pixbuf);
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_get_rowstride(IntPtr pixbuf);
    [DllImport(Pixbuf)] private static extern int gdk_pixbuf_get_n_channels(IntPtr pixbuf);
    [DllImport(Pixbuf)] private static extern IntPtr gdk_pixbuf_get_pixels(IntPtr pixbuf);
    [DllImport(Pixbuf)]
    private static extern int gdk_pixbuf_savev(IntPtr pixbuf, [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string type, IntPtr optionKeys, IntPtr optionValues, out IntPtr error);
    [DllImport(Indicator)]
    private static extern IntPtr app_indicator_new([MarshalAs(UnmanagedType.LPUTF8Str)] string id,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string iconName, int category);
    [DllImport(Indicator)]
    private static extern void app_indicator_set_icon_theme_path(IntPtr indicator, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Indicator)]
    private static extern void app_indicator_set_icon_full(IntPtr indicator, [MarshalAs(UnmanagedType.LPUTF8Str)] string iconName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string description);
    [DllImport(Indicator)]
    private static extern void app_indicator_set_title(IntPtr indicator, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport(Indicator)] private static extern void app_indicator_set_status(IntPtr indicator, int status);
    [DllImport(Indicator)] private static extern void app_indicator_set_menu(IntPtr indicator, IntPtr menu);
}
#endif
