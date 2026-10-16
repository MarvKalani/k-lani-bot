using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KLaniBot.CartridgeLab;

// The native lab supplies stable controls with changing semantics to private accessibility regressions.
internal static partial class GtkFixture
{
    private const int DynamicWindowWidth = 640;
    private const int DynamicWindowHeight = 420;
    private static readonly JsonSerializerOptions ResultOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly DynamicFormModel DynamicModel = new();
    private static IntPtr dynamicAddress;
    private static IntPtr dynamicOptions;
    private static IntPtr dynamicContext;
    private static IntPtr dynamicSavedState;
    private static bool updatingOptions;

    // Throws InvalidOperationException when the private GTK display is unavailable.
    public static int RunDynamicForm(string resultPath)
    {
        if (gtk_init_check(IntPtr.Zero, IntPtr.Zero) == 0)
        {
            throw new InvalidOperationException("GTK3 display required.");
        }

        var window = Window("k-lani-bot · Synthetic dynamic form", DynamicWindowWidth, DynamicWindowHeight, out var box);
        Label(box, "SYNTHETIC · No delivery, external files or network actions");
        Button(box, "Local", () => ChangeDynamicMode(DeliveryMode.Local));
        Button(box, "Delivery", () => ChangeDynamicMode(DeliveryMode.Delivery));
        dynamicOptions = gtk_combo_box_text_new();
        Name(dynamicOptions, "Delivery option");
        gtk_box_pack_start(box, dynamicOptions, 0, 0, 0);
        Hook(dynamicOptions, "changed", (_, _) => SelectDynamicOption());
        dynamicAddress = gtk_entry_new();
        Name(dynamicAddress, "Delivery address");
        gtk_box_pack_start(box, dynamicAddress, 0, 0, 0);
        Hook(dynamicAddress, "changed", (_, _) => DynamicModel.TrySetAddress(Text(dynamicAddress)));
        var orderActions = DynamicGroup(box, "Order actions");
        Button(orderActions, "Apply", () => OpenDynamicConfirmation(window, resultPath));
        var previewActions = DynamicGroup(box, "Preview actions");
        Button(previewActions, "Apply", () => { });
        AddDynamicOutputs(box);
        Hook(window, "destroy", (_, _) => gtk_main_quit());
        gtk_widget_show_all(window);
        RefreshDynamicFields();
        gtk_main();

        return 0;
    }

    private static void AddDynamicOutputs(IntPtr box)
    {
        dynamicContext = gtk_label_new("Local");
        Name(dynamicContext, "Delivery context");
        atk_object_set_accessible_id(gtk_widget_get_accessible(dynamicContext), "delivery.context");
        gtk_box_pack_start(box, dynamicContext, 0, 0, 0);
        dynamicSavedState = gtk_label_new("");
        Name(dynamicSavedState, "Saved order state");
        atk_object_set_accessible_id(gtk_widget_get_accessible(dynamicSavedState), "delivery.saved-state");
        gtk_box_pack_start(box, dynamicSavedState, 0, 0, 0);
    }

    private static IntPtr DynamicGroup(IntPtr parent, string name)
    {
        var frame = gtk_frame_new(name);
        Name(frame, name);
        var box = gtk_box_new(1, 8);
        gtk_container_add(frame, box);
        gtk_box_pack_start(parent, frame, 0, 0, 0);

        return box;
    }

    private static void ChangeDynamicMode(DeliveryMode mode)
    {
        if (!DynamicModel.TryChangeMode(mode))
        {
            return;
        }

        RefreshDynamicFields();
    }

    private static void RefreshDynamicFields()
    {
        ShowDynamicContext();
        updatingOptions = true;
        gtk_combo_box_text_remove_all(dynamicOptions);
        foreach (var option in DynamicModel.Options)
        {
            gtk_combo_box_text_append_text(dynamicOptions, option.ToString());
        }
        gtk_combo_box_set_active(dynamicOptions, 0);
        updatingOptions = false;
        gtk_entry_set_text(dynamicAddress, "");
        if (DynamicModel.AddressVisible)
        {
            gtk_widget_show(dynamicAddress);
        }
        else
        {
            gtk_widget_hide(dynamicAddress);
        }
    }

    private static void SelectDynamicOption()
    {
        if (updatingOptions)
        {
            return;
        }

        int index = gtk_combo_box_get_active(dynamicOptions);
        if (index >= 0 && index < DynamicModel.Options.Length)
        {
            DynamicModel.TrySelectOption(DynamicModel.Options[index], DynamicModel.Revision);
            ShowDynamicContext();
        }
    }

    private static void OpenDynamicConfirmation(IntPtr parent, string resultPath)
    {
        if (!DynamicModel.TryOpenConfirmation())
        {
            return;
        }

        var dialog = Window("k-lani-bot · Confirm synthetic order", DynamicWindowWidth, DynamicWindowHeight, out var box);
        gtk_window_set_transient_for(dialog, parent);
        gtk_window_set_modal(dialog, 1);
        Label(box, "Confirm the observed synthetic order");
        Button(box, "Confirm", () => SaveDynamicResult(dialog, resultPath));
        Button(box, "Cancel", () => gtk_widget_destroy(dialog));
        Hook(dialog, "destroy", (_, _) => DynamicModel.CancelConfirmation());
        gtk_widget_show_all(dialog);
    }

    private static void ShowDynamicContext()
    {
        gtk_label_set_text(dynamicContext, DynamicModel.Mode + " / " + DynamicModel.Option);
    }

    // Throws IOException or UnauthorizedAccessException when the synthetic output cannot be persisted.
    private static void SaveDynamicResult(IntPtr dialog, string resultPath)
    {
        if (!DynamicModel.TryConfirm(out var result))
        {
            return;
        }

        File.WriteAllText(resultPath, JsonSerializer.Serialize(result, ResultOptions));
        gtk_label_set_text(dynamicSavedState, JsonSerializer.Serialize(result, ResultOptions));
        gtk_widget_destroy(dialog);
    }

    [DllImport(Gtk)] private static extern IntPtr gtk_frame_new(string label);
    [DllImport(Gtk)] private static extern IntPtr gtk_combo_box_text_new();
    [DllImport(Gtk)] private static extern void gtk_combo_box_text_remove_all(IntPtr widget);
    [DllImport(Gtk)] private static extern void gtk_combo_box_text_append_text(IntPtr widget, string text);
    [DllImport(Gtk)] private static extern void gtk_combo_box_set_active(IntPtr widget, int index);
    [DllImport(Gtk)] private static extern int gtk_combo_box_get_active(IntPtr widget);
    [DllImport(Gtk)] private static extern void gtk_widget_show(IntPtr widget);
    [DllImport(Gtk)] private static extern void gtk_widget_hide(IntPtr widget);
}
