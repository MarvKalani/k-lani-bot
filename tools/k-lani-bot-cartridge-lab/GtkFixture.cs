using System.Runtime.InteropServices;

namespace KLaniBot.CartridgeLab;

// The cartridge lab's GTK window: a synthetic slicer with buttons for each operation, for cartridge and guard tests in a private display.
internal static partial class GtkFixture
{
    private const string Gtk = "libgtk-3.so.0", Obj = "libgobject-2.0.so.0";
    // GTK_RESPONSE_ACCEPT: the response code the dismiss button gives gtk_dialog_run.
    private const int DismissResponse = -3;
    private static readonly List<Delegate> Callbacks = [];
    // The GTK signal handler signature for button clicks.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Callback(IntPtr widget, IntPtr data);
    private static IntPtr status;
    // Throws InvalidOperationException without a GTK display.
    public static int Run(FixtureModel model, string profile)
    {
        if (gtk_init_check(IntPtr.Zero, IntPtr.Zero) == 0)
            throw new InvalidOperationException("GTK3 display required.");
        var window = Window("k-lani-bot · OrcaSlicer 2.4.2 Test Fixture", 720, 500, out var box);
        Label(box, "TEST DOUBLE · No real slicing, files, printer or network actions");
        Label(box, "Operator profile: " + profile);
        Label(box, "Printer: k-lani-bot Synthetic Printer");
        Label(box, "Filament: k-lani-bot Synthetic PLA");
        Label(box, "Process: " + model.ProcessProfile);
        Button(box, "Import 3MF/STL/STEP/SVG/OBJ/AMF...", () => ShowPicker(model, window));
        Button(box, "Open archive dialog...", () => RunBlockingDialog(window));
        var edit = gtk_entry_new();
        gtk_entry_set_text(edit, model.ProcessProfile);
        Name(edit, "Process profile edit");
        gtk_box_pack_start(box, edit, 0, 0, 0);
        Button(
            box,
            "Edit process profile",
            ()
                =>
                {
                    bool ok = model.Apply("edit", Text(edit));
                    Status(ok ? "Process edited: " + model.ProcessProfile : "Denied: application.edit");
                });
        Button(box, "Delete master data", () => { model.Apply("delete-master-data"); Status("Denied: application.delete"); });
        Button(box, "Print plate", () => { model.Apply("print"); Status("Denied: device.print"); });
        status = gtk_label_new("Imported model: none");
        Name(status, "Fixture result");
        gtk_box_pack_start(box, status, 0, 0, 0);
        Hook(window, "destroy", (_, _) => gtk_main_quit());
        gtk_widget_show_all(window);
        gtk_main();
        return 0;
    }
    private static void ShowPicker(FixtureModel model, IntPtr parent)
    {
        var dialog = Window("k-lani-bot · Synthetic file picker", 640, 360, out var box);
        gtk_window_set_transient_for(dialog, parent);
        gtk_window_set_modal(dialog, 1);
        Label(box, "Synthetic file picker · operations affect in-memory names only");
        var path = gtk_entry_new();
        Name(path, "Synthetic model path");
        gtk_box_pack_start(box, path, 0, 0, 0);
        Label(box, "synthetic-cube.stl / synthetic-do-not-delete.txt");
        Button(
            box,
            "Delete file",
            () => { bool ok = model.Apply("delete-file"); Status(ok ? "Synthetic delete logged" : "Denied: file.delete"); });
        Button(
            box,
            "Rename file",
            () => { bool ok = model.Apply("rename-file"); Status(ok ? "Synthetic rename logged" : "Denied: file.rename"); });
        Button(
            box,
            "Change properties",
            () => { bool ok = model.Apply("properties"); Status(ok ? "Synthetic properties logged" : "Denied: file.properties"); });
        Button(
            box,
            "Open",
            () =>
            {
                var value = Text(path);
                if (!value.EndsWith(".stl", StringComparison.OrdinalIgnoreCase))
                {
                    Status("Rejected: fixture accepts STL only");
                    return;
                }
                bool ok = model.Apply("import", value);
                Status(ok ? "Imported model: " + model.ImportedModel : "Denied: model.import");
                if (ok)
                    gtk_widget_destroy(dialog);
            });
        Button(box, "Cancel", () => gtk_widget_destroy(dialog));
        gtk_widget_show_all(dialog);
    }
    // Many GTK3 programs open a dialog with gtk_dialog_run from inside the click handler, as Xarchiver does for
    // "New". The handler then does not return until the dialog closes, and the accessibility bridge runs it inside
    // the dispatch of the connection that delivered the action.
    private static void RunBlockingDialog(IntPtr parent)
    {
        var dialog = gtk_dialog_new();
        gtk_window_set_title(dialog, "k-lani-bot · Blocking dialog");
        gtk_window_set_transient_for(dialog, parent);
        gtk_window_set_modal(dialog, 1);
        Label(gtk_dialog_get_content_area(dialog), "Synthetic dialog that runs its own main loop");
        gtk_dialog_add_button(dialog, "Dismiss", DismissResponse);
        gtk_widget_show_all(dialog);
        gtk_dialog_run(dialog);
        gtk_widget_destroy(dialog);
        Status("Blocking dialog dismissed");
    }
    private static IntPtr Window(string title, int width, int height, out IntPtr box)
    {
        var w = gtk_window_new(0);
        gtk_window_set_title(w, title);
        gtk_window_set_default_size(w, width, height);
        box = gtk_box_new(1, 8);
        gtk_container_set_border_width(box, 16);
        gtk_container_add(w, box);
        return w;
    }
    private static void Name(IntPtr widget, string name) => atk_object_set_name(gtk_widget_get_accessible(widget), name);
    private static void Label(IntPtr box, string text) => gtk_box_pack_start(box, gtk_label_new(text), 0, 0, 0);
    private static void Button(IntPtr box, string title, Action action)
    {
        var b = gtk_button_new_with_label(title);
        gtk_box_pack_start(box, b, 0, 0, 0);
        Hook(b, "clicked", (_, _) => action());
    }
    private static void Status(string text) => gtk_label_set_text(status, text);
    private static string Text(IntPtr edit) => Marshal.PtrToStringUTF8(gtk_entry_get_text(edit)) ?? "";
    private static void Hook(IntPtr widget, string signal, Callback callback)
    {
        Callbacks.Add(callback);
        g_signal_connect_data(widget, signal, Marshal.GetFunctionPointerForDelegate(callback), IntPtr.Zero, IntPtr.Zero, 0);
    }
    [DllImport(Gtk)] private static extern int gtk_init_check(IntPtr argc, IntPtr argv);
    [DllImport(Gtk)] private static extern void gtk_main();
    [DllImport(Gtk)] private static extern void gtk_main_quit();
    [DllImport(Gtk)] private static extern IntPtr gtk_dialog_new();
    [DllImport(Gtk)] private static extern IntPtr gtk_dialog_get_content_area(IntPtr dialog);
    [DllImport(Gtk)]
    private static extern IntPtr gtk_dialog_add_button(
        IntPtr dialog,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string title,
        int responseId);
    [DllImport(Gtk)] private static extern int gtk_dialog_run(IntPtr dialog);
    [DllImport(Gtk)] private static extern IntPtr gtk_window_new(int type);
    [DllImport(Gtk)] private static extern void gtk_window_set_title(IntPtr w, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport(Gtk)] private static extern void gtk_window_set_default_size(IntPtr w, int width, int height);
    [DllImport(Gtk)] private static extern void gtk_window_set_transient_for(IntPtr w, IntPtr parent);
    [DllImport(Gtk)] private static extern void gtk_window_set_modal(IntPtr w, int modal);
    [DllImport(Gtk)] private static extern IntPtr gtk_box_new(int orientation, int spacing);
    [DllImport(Gtk)] private static extern void gtk_box_pack_start(IntPtr box, IntPtr child, int expand, int fill, uint padding);
    [DllImport(Gtk)] private static extern void gtk_container_set_border_width(IntPtr container, uint width);
    [DllImport(Gtk)] private static extern void gtk_container_add(IntPtr container, IntPtr child);
    [DllImport(Gtk)] private static extern IntPtr gtk_label_new([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Gtk)] private static extern void gtk_label_set_text(IntPtr label, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Gtk)] private static extern IntPtr gtk_button_new_with_label([MarshalAs(UnmanagedType.LPUTF8Str)] string title);
    [DllImport(Gtk)] private static extern IntPtr gtk_entry_new();
    [DllImport(Gtk)] private static extern void gtk_entry_set_text(IntPtr entry, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(Gtk)] private static extern IntPtr gtk_entry_get_text(IntPtr entry);
    [DllImport(Gtk)] private static extern IntPtr gtk_widget_get_accessible(IntPtr widget);
    [DllImport("libatk-1.0.so.0")]
    private static extern void atk_object_set_name(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Gtk)] private static extern void gtk_widget_show_all(IntPtr widget);
    [DllImport(Gtk)] private static extern void gtk_widget_destroy(IntPtr widget);
    [DllImport(Obj)]
    private static extern ulong g_signal_connect_data(
        IntPtr instance,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string signal,
        IntPtr handler,
        IntPtr data,
        IntPtr destroy,
        int flags);
}
