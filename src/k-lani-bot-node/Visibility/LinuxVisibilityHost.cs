using System.Diagnostics;

#if !WINDOWS
using System.Runtime.InteropServices;
using KLaniBot.Startup;
using KLaniBot.Guard;
using KLaniBot.Localization;
namespace KLaniBot.Visibility;

// The Linux indicator with GTK 3: a tray icon where the desktop has a tray host, otherwise a small panel window
// that stays visible, plus a red border on every monitor that does not take input. Under Wayland the
// compositor decides where windows go and cannot guarantee the border, so it is refused there; with the border
// switched on that stops all actions, and the person must switch the border off locally to work without it.
public sealed partial class LinuxVisibilityHost : NativeVisibilityHost
{
    private const string Gtk = "libgtk-3.so.0", Gdk = "libgdk-3.so.0", Obj = "libgobject-2.0.so.0";
    private readonly IntPtr _tray, _panel, _label;
    private readonly IntPtr _panelImage; private readonly IntPtr _iconImage; private readonly List<IntPtr> _borders = new();
    private IntPtr _assignmentWindow;
    private IntPtr _assignmentLabel;
    private string _monitorGeometry = "";
    private readonly List<Delegate> _callbacks = new();
    private readonly List<Delegate> _menuCallbacks = new();
    private readonly List<Delegate> _questionCallbacks = new();
    private IntPtr _menu;
    private IntPtr _question;
    private string? _questionId;
    private string _menuState = "";
    // The GTK signal handler for menu items and buttons.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Callback(IntPtr widget, IntPtr data);
    // The GTK handler that opens the tray menu.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PopupCallback(IntPtr icon, uint button, uint time, IntPtr data);
    // The GTK handler for closing the question window.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeleteCallback(IntPtr widget, IntPtr ev, IntPtr data);
    // Throws InvalidOperationException without a GTK desktop.
    public LinuxVisibilityHost()
    {
        if (gtk_init_check(IntPtr.Zero, IntPtr.Zero) == 0)
            throw new InvalidOperationException("An interactive GTK desktop is required for the permanent indicator.");
        _panelImage = LoadProductImage(ProductImage.LinuxPanel);
        _iconImage = LoadProductImage(ProductImage.Icon24);
        _tray = gtk_status_icon_new();
        gtk_status_icon_set_title(_tray, "k-lani-bot-node");
        gtk_status_icon_set_visible(_tray, 1);
        Hook(_tray, "activate", (_, _) => Popup());
        var pop = new PopupCallback((_, _, _, _) => Popup());
        _callbacks.Add(pop);
        g_signal_connect_data(_tray, "popup-menu", Marshal.GetFunctionPointerForDelegate(pop), IntPtr.Zero, IntPtr.Zero, 0);
        _panel = gtk_window_new(0);
        gtk_window_set_title(_panel, "k-lani-bot-node · Status");
        gtk_window_set_default_size(_panel, 380, 155);
        gtk_window_set_resizable(_panel, 0);
        gtk_window_set_keep_above(_panel, 1);
        _label = gtk_label_new(UiTexts.Get(UiText.PanelReadyWithoutClient));
        var panelBox = gtk_box_new(1, 4);
        gtk_container_add(_panel, panelBox);
        gtk_box_pack_start(panelBox, gtk_image_new_from_pixbuf(_panelImage), 0, 0, 0);
        gtk_box_pack_start(panelBox, gtk_label_new(UiTexts.Get(UiText.PanelSubtitle)), 0, 0, 0);
        gtk_box_pack_start(panelBox, _label, 0, 0, 0);
        var menuButton = gtk_button_new_with_label(UiTexts.Get(UiText.PanelSettingsButton));
        gtk_box_pack_start(panelBox, menuButton, 0, 0, 0);
        Hook(menuButton, "clicked", (_, _) => Popup());
        var delete = new DeleteCallback((_, _, _) => { Revoke(); return 1; });
        _callbacks.Add(delete);
        g_signal_connect_data(_panel, "delete-event", Marshal.GetFunctionPointerForDelegate(delete), IntPtr.Zero, IntPtr.Zero, 0);
        gtk_widget_show_all(_panel);
    }
    // Throws InvalidDataException when the image cannot be decoded.
    private static IntPtr LoadProductImage(ProductImage image)
    {
        var loader = gdk_pixbuf_loader_new();
        try
        {
            byte[] bytes = ProductBranding.Read(image);
            if (gdk_pixbuf_loader_write(loader, bytes, (nuint)bytes.Length, out var error) == 0
                || gdk_pixbuf_loader_close(loader, out error) == 0)
            {
                if (error != IntPtr.Zero)
                    g_error_free(error);
                throw new InvalidDataException("The product logo could not be decoded.");
            }
            return g_object_ref(gdk_pixbuf_loader_get_pixbuf(loader));
        }
        finally { g_object_unref(loader); }
    }
    private static void ApplyStatusBadge(IntPtr icon, uint rgba)
    {
        IntPtr pixels = gdk_pixbuf_get_pixels(icon);
        int rowStride = gdk_pixbuf_get_rowstride(icon);
        int channels = gdk_pixbuf_get_n_channels(icon);
        for (int y = 17; y < 24; y++)
        {
            for (int x = 17; x < 24; x++)
            {
                int offset = y * rowStride + x * channels;
                Marshal.WriteByte(pixels, offset, (byte)(rgba >> 24));
                Marshal.WriteByte(pixels, offset + 1, (byte)(rgba >> 16));
                Marshal.WriteByte(pixels, offset + 2, (byte)(rgba >> 8));
                if (channels == 4)
                    Marshal.WriteByte(pixels, offset + 3, 255);
            }
        }
    }
    private void Hook(IntPtr widget, string signal, Callback callback, List<Delegate>? keep = null)
    {
        (keep ?? _callbacks).Add(callback);
        g_signal_connect_data(widget, signal, Marshal.GetFunctionPointerForDelegate(callback), IntPtr.Zero, IntPtr.Zero, 0);
    }
    private void Popup()
    {
        if (_menu != IntPtr.Zero)
            gtk_menu_popup_at_pointer(_menu, IntPtr.Zero);
    }
    private void Item(string label, Action action, bool? check = null)
    {
        var item = check.HasValue ? gtk_check_menu_item_new_with_label(label) : gtk_menu_item_new_with_label(label);
        if (check.HasValue)
            gtk_check_menu_item_set_active(item, check.Value ? 1 : 0);
        Hook(item, "activate", (_, _) => action(), _menuCallbacks);
        if (check.HasValue && Current.Status == VisibilityStatus.Active)
            gtk_widget_set_sensitive(item, 0);
        gtk_menu_shell_append(_menu, item);
    }
    private static uint StatusBadgeColor(VisibilityStatus status)
    {
        return status switch
        {
            VisibilityStatus.Active => 0xff3939ff,
            VisibilityStatus.Connected => 0x379eefff,
            VisibilityStatus.Idle => 0x888888ff,
            _ => throw new UnreachableException()
        };
    }
    private static string StatusHexColor(VisibilityStatus status)
    {
        return status switch
        {
            VisibilityStatus.Active => "#ff3939",
            VisibilityStatus.Connected => "#379eef",
            VisibilityStatus.Idle => "#888888",
            _ => throw new UnreachableException()
        };
    }
    // Throws NotSupportedException on Wayland, where the border is not guaranteed, and InvalidOperationException when border or banner
    // cannot be shown.
    protected override void Apply(VisibilityState state)
    {
        // The node uses its tray when one is available. A persistent branded
        // fallback is necessary on desktops without a tray host; the controller
        // never constructs this native host.
        if (gtk_status_icon_is_embedded(_tray) != 0)
            gtk_widget_hide(_panel);
        else if (gtk_widget_get_visible(_panel) == 0)
            gtk_widget_show_all(_panel);
        UpdateTrayAndPanel(state);
        // A panel is kept visible even if the desktop has no XEmbed tray host.
        string stamp = state.Status.ToString() + state.Unattended + state.ScreenBorder + state.AccessLocked + state.Prompt?.Challenge
            + state.Presentation + state.Pace;
        if (stamp != _menuState)
        {
            _menuState = stamp;
            RebuildMenu(state);
        }
        ApplyBorder(state);
        RaiseIndicators(state);
    }

    private void UpdateTrayAndPanel(VisibilityState state)
    {
        uint color = StatusBadgeColor(state.Status);
        // The badge is painted into the pixels, so it goes onto a copy and the original stays clean.
        var pixbuf = gdk_pixbuf_copy(_iconImage);
        ApplyStatusBadge(pixbuf, color);
        gtk_status_icon_set_from_pixbuf(_tray, pixbuf);
        g_object_unref(pixbuf);
        gtk_status_icon_set_tooltip_text(
            _tray,
            ProductIdentity.ProductName + " " + ProductIdentity.DisplayVersion + " · " + StatusText(state));
        string hex = StatusHexColor(state.Status);
        gtk_label_set_markup(_label, "<span foreground=\"" + hex + "\">● " + StatusText(state) + "</span>");
    }

    private void RebuildMenu(VisibilityState state)
    {
        if (_menu != IntPtr.Zero)
            gtk_widget_destroy(_menu);
        _menuCallbacks.Clear();
        _menu = gtk_menu_new();
        AppendInformation(state);
        if (state.Prompt != null)
        {
            Item(UiTexts.Get(UiText.AllowThisSession), () => Decide(SessionConsent.Allowed));
            Item(UiTexts.Get(UiText.NotNow), () => Decide(SessionConsent.Denied));
            Item(UiTexts.Get(UiText.AskAgainInFiveMinutes), () => Decide(SessionConsent.Deferred));
        }
        Item(UiTexts.Get(UiText.MenuPause), PauseAgent, state.Pace == Pace.Paused);
        Item(UiTexts.Get(UiText.MenuSlowMotion), SlowMotion, state.Pace == Pace.SlowMotion);
        Item(UiTexts.Get(UiText.MenuNormalSpeed), NormalSpeed, state.Pace == Pace.Normal);
        Item(UiTexts.Get(UiText.ShowPendingRequest), ShowPending);
        Item(UiTexts.Get(UiText.AllowAutomatically), SetUnattended, state.Unattended);
        Item(
            UiTexts.Get(UiText.OperateInBackground),
            SetBackgroundInteraction,
            state.WindowInteraction == WindowInteractionMode.BackgroundSemantic);
        Item(UiTexts.Get(UiText.AllowModalDialogs), SetModalInteraction, state.ModalDialogs == ModalDialogMode.AllowInteraction);
        Item(UiTexts.Get(UiText.RecordScreenshots), SetScreenshotRecording, state.RecordActionScreenshots);
        Item(UiTexts.Get(UiText.OpenTrace), OpenTraceViewer);
        Item(UiTexts.Get(UiText.ShowScreenBorder), SetBorder, state.ScreenBorder);
        Item(AccessLockLabel, ToggleAccessLock, state.AccessLocked);
        Item(UiTexts.Get(UiText.QuitProgram), Quit);
        gtk_widget_show_all(_menu);
    }

    // Greyed-out lines at the top of the menu: version, node, master and, during a session, who works and why.
    private void AppendInformation(VisibilityState state)
    {
        var lines = new List<string>
        {
            UiTexts.Format(UiText.MenuVersionAndNode, ProductIdentity.DisplayVersion, ShownNodeId),
            UiTexts.Format(UiText.MenuMaster, ShownMasterOf(state))
        };
        if (state.Presentation != null)
        {
            lines.Add(UiTexts.Format(UiText.MenuClient, state.Presentation.ShownClient));
            lines.Add(UiTexts.Format(UiText.MenuPurpose, state.Presentation.ShownPurpose));
        }

        foreach (string text in lines)
        {
            var line = gtk_menu_item_new_with_label(text);
            gtk_widget_set_sensitive(line, 0);
            gtk_menu_shell_append(_menu, line);
        }
    }

    // Throws NotSupportedException on Wayland, where the border is not guaranteed.
    private void ApplyBorder(VisibilityState state)
    {
        List<MonitorArea> monitors = [];
        if (state.Border)
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
                throw new NotSupportedException(
                    "The Wayland compositor cannot guarantee this border. Keep access blocked or explicitly opt out locally.");
            monitors = ReadMonitors();
        }
        // The banner grows with the step list, so a different number of lines recreates it.
        string geometry = string.Join(";", monitors.Select(DescribeMonitor)) + "|" + AssignmentLines(state).Count;
        if (_borders.Count > 0 && (!state.Border || geometry != _monitorGeometry))
        {
            CloseActivityWindows();
        }
        _monitorGeometry = geometry;

        if (state.Border && _borders.Count == 0)
        {
            foreach (MonitorArea monitor in monitors)
            {
                AddBorder(monitor);
            }
            MonitorArea primary = monitors.Single(monitor => monitor.Primary);
            CreateAssignmentWindow(primary.Area, BorderThickness(primary), state);
            gdk_display_sync(gdk_display_get_default());
        }
    }

    // A border or banner that did not appear stops the activity instead of letting it run unseen.
    // Throws InvalidOperationException when border or banner is not visible.
    private void RaiseIndicators(VisibilityState state)
    {
        foreach (var window in _borders)
        {
            if (gtk_widget_get_visible(window) == 0)
                throw new InvalidOperationException("Activity border unavailable.");
            gdk_window_raise(gtk_widget_get_window(window));
        }
        if (_assignmentWindow != IntPtr.Zero)
        {
            gtk_label_set_text(_assignmentLabel, DescribeAssignment(state));
            if (gtk_widget_get_visible(_assignmentWindow) == 0)
            {
                throw new InvalidOperationException("Visible assignment indicator unavailable.");
            }
            gdk_window_raise(gtk_widget_get_window(_assignmentWindow));
        }
    }

    // Every monitor of the desktop gets the border, so the person sees that an AI is working whichever screen they
    // look at. The assignment banner goes on the primary monitor only. A monitor GTK reports without a usable size
    // stops the indicator instead of being skipped, because a skipped screen would show nothing.
    // Throws InvalidOperationException when a monitor has no usable size or none is found.
    private List<MonitorArea> ReadMonitors()
    {
        IntPtr display = gdk_display_get_default();
        IntPtr primary = gdk_display_get_primary_monitor(display);
        int count = gdk_display_get_n_monitors(display);
        var monitors = new List<MonitorArea>();
        for (int index = 0; index < count; index++)
        {
            IntPtr monitor = gdk_display_get_monitor(display, index);
            if (monitor == IntPtr.Zero)
                throw new InvalidOperationException("Monitor unavailable.");
            gdk_monitor_get_geometry(monitor, out Geometry area);
            if (area.Width <= 0 || area.Height <= 0)
                throw new InvalidOperationException("Monitor unavailable.");
            int scale = Math.Max(1, gdk_monitor_get_scale_factor(monitor));
            monitors.Add(new MonitorArea(area, scale, monitor == primary));
        }
        if (monitors.Count == 0)
            throw new InvalidOperationException("Monitor unavailable.");
        // X11 does not always name a primary monitor; the first one then carries the banner.
        if (!monitors.Any(monitor => monitor.Primary))
            monitors[0] = monitors[0] with
            {
                Primary = true
            };
        ReplaceMonitorsInTest(ref monitors);
        return monitors;
    }

    // A private X server cannot be made to report several monitors to GTK, so the regression supplies them here.
    // Only the test project implements this; in the node the call does not exist.
    partial void ReplaceMonitorsInTest(ref List<MonitorArea> monitors);

    private static string DescribeMonitor(MonitorArea monitor)
    {
        Geometry area = monitor.Area;
        return $"{area.X}:{area.Y}:{area.Width}:{area.Height}:{monitor.Scale}:{monitor.Primary}";
    }

    private static int BorderThickness(MonitorArea monitor)
    {
        return Math.Max(1, 4 / monitor.Scale);
    }

    private void AddBorder(MonitorArea monitor)
    {
        Geometry area = monitor.Area;
        int p = BorderThickness(monitor);
        Geometry[] edges =
        [
            new Geometry(area.X, area.Y, area.Width, p),
            new Geometry(area.X, area.Y + area.Height - p, area.Width, p),
            new Geometry(area.X, area.Y, p, area.Height),
            new Geometry(area.X + area.Width - p, area.Y, p, area.Height)
        ];
        foreach (Geometry edge in edges)
        {
            int x = edge.X;
            int y = edge.Y;
            int width = edge.Width;
            int height = edge.Height;
            var window = gtk_window_new(1);
            gtk_window_set_title(window, "k-lani-bot · activity border");
            gtk_window_set_accept_focus(window, 0);
            gtk_window_set_focus_on_map(window, 0);
            gtk_window_set_decorated(window, 0);
            gtk_window_set_keep_above(window, 1);
            gtk_widget_set_size_request(window, width, height);
            gtk_window_move(window, x, y);
            var red = new Rgba { Red = 1, Green = .1, Blue = .1, Alpha = 1 };
            gtk_widget_override_background_color(window, 0, ref red);
            gtk_widget_show_all(window);
            var native = gtk_widget_get_window(window);
            var empty = cairo_region_create();
            gdk_window_input_shape_combine_region(native, empty, 0, 0);
            cairo_region_destroy(empty);
            gdk_window_set_pass_through(native, 1);
            gdk_window_raise(native);
            gdk_window_process_updates(native, 1);
            _borders.Add(window);
        }
    }

    private const int AssignmentLineHeight = 20;
    private const int AssignmentPadding = 16;

    private void CreateAssignmentWindow(Geometry area, int thickness, VisibilityState state)
    {
        string text = DescribeAssignment(state);
        int textHeight = AssignmentLines(state).Count * AssignmentLineHeight;
        int width = Math.Min(1000, area.Width - 32);
        _assignmentWindow = gtk_window_new(1);
        gtk_window_set_title(_assignmentWindow, UiTexts.Get(UiText.AssignmentWindowTitle));
        gtk_window_set_accept_focus(_assignmentWindow, 0);
        gtk_window_set_focus_on_map(_assignmentWindow, 0);
        gtk_window_set_decorated(_assignmentWindow, 0);
        gtk_window_set_keep_above(_assignmentWindow, 1);
        gtk_widget_set_size_request(_assignmentWindow, width, textHeight + AssignmentPadding);
        gtk_window_move(_assignmentWindow, area.X + (area.Width - width) / 2, area.Y + thickness);
        gtk_container_set_border_width(_assignmentWindow, 8);
        _assignmentLabel = gtk_label_new(text);
        gtk_label_set_xalign(_assignmentLabel, 0);
        gtk_label_set_ellipsize(_assignmentLabel, 3);
        gtk_widget_set_size_request(_assignmentLabel, width - AssignmentPadding, textHeight);
        var background = new Rgba { Red = .55, Green = .04, Blue = .04, Alpha = 1 };
        var foreground = new Rgba { Red = 1, Green = 1, Blue = 1, Alpha = 1 };
        gtk_widget_override_background_color(_assignmentWindow, 0, ref background);
        gtk_widget_override_color(_assignmentLabel, 0, ref foreground);
        gtk_container_add(_assignmentWindow, _assignmentLabel);
        gtk_widget_show_all(_assignmentWindow);
        IntPtr native = gtk_widget_get_window(_assignmentWindow);
        IntPtr empty = cairo_region_create();
        gdk_window_input_shape_combine_region(native, empty, 0, 0);
        cairo_region_destroy(empty);
        gdk_window_set_pass_through(native, 1);
        gdk_window_raise(native);
        gdk_window_process_updates(native, 1);
    }

    private void CloseActivityWindows()
    {
        if (_assignmentWindow != IntPtr.Zero)
        {
            gtk_widget_destroy(_assignmentWindow);
            _assignmentWindow = IntPtr.Zero;
            _assignmentLabel = IntPtr.Zero;
        }
        foreach (IntPtr window in _borders)
        {
            gtk_widget_destroy(window);
        }
        _borders.Clear();
    }
    protected override void ShowQuestion(ConsentPrompt? prompt)
    {
        if (prompt == null)
        {
            if (_question != IntPtr.Zero)
                gtk_widget_destroy(_question);
            _question = IntPtr.Zero;
            _questionId = null;
            _questionCallbacks.Clear();
            return;
        }
        if (_questionId == prompt.Challenge && _question != IntPtr.Zero)
        {
            gtk_window_present(_question);
            return;
        }
        if (_question != IntPtr.Zero)
            gtk_widget_destroy(_question);
        _questionCallbacks.Clear();
        _questionId = prompt.Challenge;
        _question = CreateQuestionWindow(prompt);
        gtk_widget_show_all(_question);
    }

    // Closing the window only hides it: the question stays open in the menu until it is answered.
    private IntPtr CreateQuestionWindow(ConsentPrompt prompt)
    {
        IntPtr question = gtk_window_new(0);
        gtk_window_set_title(question, UiTexts.Get(UiText.ConsentWindowTitle));
        gtk_window_set_keep_above(question, 1);
        gtk_window_set_default_size(question, QuestionWidth, QuestionHeight);
        var box = gtk_box_new(1, 10);
        gtk_container_set_border_width(box, 16);
        gtk_container_add(question, box);
        var notice = gtk_label_new(DescribeConsent(prompt));
        gtk_label_set_line_wrap(notice, 1);
        gtk_label_set_max_width_chars(notice, QuestionLineCharacters);
        gtk_box_pack_start(box, notice, 0, 0, 10);
        foreach (ConsentChoice consentChoice in ConsentChoices)
        {
            string label = UiTexts.Get(consentChoice.Label);
            SessionConsent choice = consentChoice.Decision;
            var button = gtk_button_new_with_label(label);
            Hook(button, "clicked", (_, _) => Decide(choice), _questionCallbacks);
            gtk_box_pack_start(box, button, 0, 0, 0);
        }

        var delete = new DeleteCallback((_, _, _) => { gtk_widget_hide(question); return 1; });
        _questionCallbacks.Add(delete);
        g_signal_connect_data(question, "delete-event", Marshal.GetFunctionPointerForDelegate(delete), IntPtr.Zero, IntPtr.Zero, 0);
        return question;
    }

    private const int QuestionWidth = 520;
    private const int QuestionHeight = 310;
    private const int QuestionLineCharacters = 65;
    // Wayland draws no red border, so there ESC has nothing to end.
    protected override IEscapeKeyWatch? StartEscapeKeyWatch()
    {
        bool wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        if (wayland)
        {
            return null;
        }
        if (!X11EscapeKeyWatch.TryStart(false, out X11EscapeKeyWatch? watch))
        {
            Console.Error.WriteLine("ESC cannot end sessions: the X server offers no XInput 2.");
            return null;
        }
        return watch;
    }
    protected override void Pump()
    {
        while (gtk_events_pending() != 0)
            gtk_main_iteration_do(0);
    }
    protected override void CloseNative()
    {
        if (_question != IntPtr.Zero)
            gtk_widget_destroy(_question);
        if (_menu != IntPtr.Zero)
            gtk_widget_destroy(_menu);
        CloseActivityWindows();
        gtk_widget_destroy(_panel);
        gtk_status_icon_set_visible(_tray, 0);
        g_object_unref(_tray);
        g_object_unref(_panelImage);
        g_object_unref(_iconImage);
    }
    // A colour as GTK expects it.
    [StructLayout(LayoutKind.Sequential)]
    private struct Rgba
    {
        public double Red, Green, Blue, Alpha;
    }
    [StructLayout(LayoutKind.Sequential)]
    // Laid out like GdkRectangle, which gdk_monitor_get_geometry fills.
    private struct Geometry
    {
        public int X, Y, Width, Height;

        public Geometry(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }
    // One monitor's area and scale, and whether it carries the banner.
    private readonly record struct MonitorArea(Geometry Area, int Scale, bool Primary);
    [DllImport(Gtk)] private static extern int gtk_init_check(IntPtr argc, IntPtr argv);
    [DllImport(Gtk)] private static extern int gtk_events_pending();
    [DllImport(Gtk)] private static extern int gtk_main_iteration_do(int blocking);
    [DllImport(Gtk)] private static extern IntPtr gtk_status_icon_new();
    [DllImport(Gtk)] private static extern void gtk_status_icon_set_title(IntPtr icon, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport(Gtk)] private static extern void gtk_status_icon_set_visible(IntPtr icon, int visible);
    [DllImport(Gtk)] private static extern void gtk_status_icon_set_from_pixbuf(IntPtr icon, IntPtr pixbuf);
    [DllImport(Gtk)]
    private static extern void gtk_status_icon_set_tooltip_text(IntPtr icon, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport("libgdk_pixbuf-2.0.so.0")]
    private static extern IntPtr gdk_pixbuf_new(int colorspace, int alpha, int bits, int width, int height);
    [DllImport("libgdk_pixbuf-2.0.so.0")] private static extern void gdk_pixbuf_fill(IntPtr pixbuf, uint color);
    [DllImport(Obj)] private static extern void g_object_unref(IntPtr obj);
    [DllImport(Obj)]
    private static extern ulong g_signal_connect_data(
        IntPtr obj,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string signal,
        IntPtr callback,
        IntPtr data,
        IntPtr destroy,
        int flags);
    [DllImport(Gtk)] private static extern IntPtr gtk_window_new(int type);
    [DllImport(Gtk)] private static extern void gtk_window_set_title(IntPtr window, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport(Gtk)] private static extern void gtk_window_set_default_size(IntPtr window, int width, int height);
    [DllImport(Gtk)] private static extern void gtk_window_set_resizable(IntPtr window, int value);
    [DllImport(Gtk)] private static extern void gtk_window_set_keep_above(IntPtr window, int value);
    [DllImport(Gtk)] private static extern void gtk_window_set_accept_focus(IntPtr window, int value);
    [DllImport(Gtk)] private static extern void gtk_window_set_focus_on_map(IntPtr window, int value);
    [DllImport(Gtk)] private static extern void gtk_window_set_decorated(IntPtr window, int value);
    [DllImport(Gtk)] private static extern void gtk_window_move(IntPtr window, int x, int y);
    [DllImport(Gtk)] private static extern void gtk_window_present(IntPtr window);
    [DllImport(Gtk)] private static extern IntPtr gtk_label_new([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Gtk)] private static extern void gtk_label_set_text(IntPtr label, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Gtk)] private static extern void gtk_label_set_xalign(IntPtr label, float alignment);
    [DllImport(Gtk)] private static extern void gtk_label_set_ellipsize(IntPtr label, int mode);
    [DllImport(Gtk)] private static extern void gtk_label_set_line_wrap(IntPtr label, int wrap);
    [DllImport(Gtk)] private static extern void gtk_label_set_max_width_chars(IntPtr label, int characters);
    [DllImport(Gtk)] private static extern void gtk_label_set_markup(IntPtr label, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Gtk)] private static extern void gtk_container_add(IntPtr container, IntPtr child);
    [DllImport(Gtk)] private static extern void gtk_container_set_border_width(IntPtr container, uint width);
    [DllImport(Gtk)] private static extern void gtk_widget_show_all(IntPtr widget);
    [DllImport(Gtk)] private static extern void gtk_widget_hide(IntPtr widget);
    [DllImport(Gtk)] private static extern void gtk_widget_destroy(IntPtr widget);
    [DllImport(Gtk)] private static extern void gtk_widget_set_sensitive(IntPtr widget, int value);
    [DllImport(Gtk)] private static extern int gtk_widget_get_visible(IntPtr widget);
    [DllImport(Gtk)] private static extern IntPtr gtk_widget_get_window(IntPtr widget);
    [DllImport(Gtk)] private static extern void gtk_widget_set_size_request(IntPtr widget, int width, int height);
    [DllImport(Gtk)] private static extern void gtk_widget_override_background_color(IntPtr widget, int state, ref Rgba color);
    [DllImport(Gtk)] private static extern void gtk_widget_override_color(IntPtr widget, int state, ref Rgba color);
    [DllImport(Gtk)] private static extern IntPtr gtk_menu_new();
    [DllImport(Gtk)] private static extern IntPtr gtk_menu_item_new_with_label([MarshalAs(UnmanagedType.LPUTF8Str)] string label);
    [DllImport(Gtk)] private static extern IntPtr gtk_check_menu_item_new_with_label([MarshalAs(UnmanagedType.LPUTF8Str)] string label);
    [DllImport(Gtk)] private static extern void gtk_check_menu_item_set_active(IntPtr item, int value);
    [DllImport(Gtk)] private static extern void gtk_menu_shell_append(IntPtr menu, IntPtr item);
    [DllImport(Gtk)] private static extern void gtk_menu_popup_at_pointer(IntPtr menu, IntPtr ev);
    [DllImport(Gtk)] private static extern IntPtr gtk_box_new(int orientation, int spacing);
    [DllImport(Gtk)] private static extern void gtk_box_pack_start(IntPtr box, IntPtr child, int expand, int fill, uint padding);
    [DllImport(Gtk)] private static extern IntPtr gtk_button_new_with_label([MarshalAs(UnmanagedType.LPUTF8Str)] string label);
    [DllImport(Gdk)] private static extern IntPtr gdk_screen_get_default();
    [DllImport(Gdk)] private static extern int gdk_screen_get_width(IntPtr screen);
    [DllImport(Gdk)] private static extern int gdk_screen_get_height(IntPtr screen);
    [DllImport(Gdk)] private static extern void gdk_window_input_shape_combine_region(IntPtr window, IntPtr region, int x, int y);
    [DllImport(Gdk)] private static extern void gdk_window_set_pass_through(IntPtr window, int value);
    [DllImport(Gdk)] private static extern void gdk_window_raise(IntPtr window);
    [DllImport(Gdk)] private static extern IntPtr gdk_display_get_default();
    [DllImport(Gdk)] private static extern IntPtr gdk_display_get_primary_monitor(IntPtr display);
    [DllImport(Gdk)] private static extern IntPtr gdk_display_get_monitor(IntPtr display, int index);
    [DllImport(Gdk)] private static extern int gdk_display_get_n_monitors(IntPtr display);
    [DllImport(Gdk)] private static extern void gdk_monitor_get_geometry(IntPtr monitor, out Geometry geometry);
    [DllImport(Gdk)] private static extern int gdk_monitor_get_scale_factor(IntPtr monitor);
    [DllImport(Gdk)] private static extern void gdk_window_process_updates(IntPtr window, int children);
    [DllImport(Gdk)] private static extern void gdk_display_sync(IntPtr display);
    [DllImport("libcairo.so.2")] private static extern IntPtr cairo_region_create();
    [DllImport("libcairo.so.2")] private static extern void cairo_region_destroy(IntPtr region);
    [DllImport(Gtk)] private static extern int gtk_status_icon_is_embedded(IntPtr icon);

    [DllImport(Gtk)] private static extern IntPtr gtk_image_new_from_pixbuf(IntPtr image);
    [DllImport(Obj)] private static extern IntPtr g_object_ref(IntPtr value);
    [DllImport("libglib-2.0.so.0")] private static extern void g_error_free(IntPtr error);
    [DllImport("libgdk_pixbuf-2.0.so.0")] private static extern IntPtr gdk_pixbuf_loader_new();
    [DllImport("libgdk_pixbuf-2.0.so.0")]
    private static extern int gdk_pixbuf_loader_write(IntPtr loader, byte[] bytes, nuint length, out IntPtr error);
    [DllImport("libgdk_pixbuf-2.0.so.0")] private static extern int gdk_pixbuf_loader_close(IntPtr loader, out IntPtr error);
    [DllImport("libgdk_pixbuf-2.0.so.0")] private static extern IntPtr gdk_pixbuf_loader_get_pixbuf(IntPtr loader);
    [DllImport("libgdk_pixbuf-2.0.so.0")] private static extern IntPtr gdk_pixbuf_copy(IntPtr image);
    [DllImport("libgdk_pixbuf-2.0.so.0")] private static extern IntPtr gdk_pixbuf_get_pixels(IntPtr image);
    [DllImport("libgdk_pixbuf-2.0.so.0")] private static extern int gdk_pixbuf_get_rowstride(IntPtr image);
    [DllImport("libgdk_pixbuf-2.0.so.0")] private static extern int gdk_pixbuf_get_n_channels(IntPtr image);

}
#endif
