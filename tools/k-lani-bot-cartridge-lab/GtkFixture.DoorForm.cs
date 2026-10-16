using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KLaniBot.CartridgeLab;

// The private door application exposes its declared field constraints through accessible descriptions, not hidden model answers.
internal static partial class GtkFixture
{
    private const int DoorWindowWidth = 640;
    private const int DoorWindowHeight = 480;
    private const string DoorWindowTitle = "k-lani-bot · Synthetic door configurator";
    private static readonly DoorFormModel DoorModel = new();
    private static IntPtr doorVariant;
    private static IntPtr doorWidth;
    private static IntPtr doorPanels;
    private static IntPtr doorGlass;
    private static IntPtr doorStatus;
    private static bool refreshingDoor;

    // AT-SPI consumers read these public declarations alongside independently queried native state and real option children.
    private sealed record DoorFieldDeclaration(
        string Format, string Field, string Context, bool Required, bool ReadOnly, string[] Dependencies);

    // Throws InvalidOperationException when the private GTK display is unavailable.
    public static int RunDoorForm(string resultPath)
    {
        if (gtk_init_check(IntPtr.Zero, IntPtr.Zero) == 0)
        {
            throw new InvalidOperationException("GTK3 display required.");
        }

        var window = Window(DoorWindowTitle, DoorWindowWidth, DoorWindowHeight, out var box);
        Label(box, "SYNTHETIC · Published door rules; no orders or network actions");
        doorVariant = DoorChoice(box, "Door model", "door.model", ChangeDoorVariant);
        doorWidth = DoorChoice(box, "Door width", "door.width", ChangeDoorWidth);
        doorPanels = DoorChoice(box, "Side panels", "door.panels", ChangeDoorPanels);
        doorGlass = DoorChoice(box, "Left glass", "door.glass", ChangeDoorGlass);
        Button(box, "Save door", () => SaveDoor(resultPath));
        doorStatus = gtk_label_new("Unsaved synthetic door");
        Name(doorStatus, "Door validation result");
        gtk_box_pack_start(box, doorStatus, 0, 0, 0);
        Hook(window, "destroy", (_, _) => gtk_main_quit());
        gtk_widget_show_all(window);
        RefreshDoor();
        gtk_main();

        return 0;
    }

    private static IntPtr DoorChoice(IntPtr box, string name, string identifier, Action action)
    {
        Label(box, name);
        var choice = gtk_combo_box_text_new();
        Name(choice, name);
        atk_object_set_accessible_id(gtk_widget_get_accessible(choice), identifier);
        gtk_box_pack_start(box, choice, 0, 0, 0);
        Hook(choice, "changed", (_, _) =>
        {
            if (!refreshingDoor)
            {
                action();
            }
        });

        return choice;
    }

    private static void RefreshDoor()
    {
        refreshingDoor = true;

        FillDoorChoices(doorVariant, Enum.GetValues<DoorVariant>(), DoorModel.Model);
        FillDoorChoices(doorWidth, DoorModel.WidthOptions, DoorModel.Width);
        FillDoorChoices(doorPanels, DoorModel.SidePanelOptions, DoorModel.SidePanels);
        FillDoorChoices(doorGlass, DoorModel.GlassOptions, DoorModel.LeftGlass);
        gtk_widget_set_sensitive(doorPanels, DoorModel.Width.HasValue ? 1 : 0);
        if (DoorModel.LeftGlassVisible)
        {
            gtk_widget_show(doorGlass);
        }
        else
        {
            gtk_widget_hide(doorGlass);
        }
        DeclareDoorField(doorVariant, "door.model", true, ["door.width", "door.panels", "door.glass"]);
        DeclareDoorField(doorWidth, "door.width", true, ["door.panels", "door.glass"]);
        DeclareDoorField(doorPanels, "door.panels", true, ["door.glass"]);
        DeclareDoorField(doorGlass, "door.glass", DoorModel.LeftGlassVisible, []);
        refreshingDoor = false;
    }

    private static void FillDoorChoices<T>(IntPtr choice, T[] options, T? selected) where T : struct, Enum
    {
        gtk_combo_box_text_remove_all(choice);

        foreach (T option in options)
        {
            string label = option is DoorWidth width ? ((int)width).ToString(CultureInfo.InvariantCulture) + " mm" : option.ToString();
            gtk_combo_box_text_append_text(choice, label);
        }
        int index = selected.HasValue ? Array.IndexOf(options, selected.Value) : -1;
        gtk_combo_box_set_active(choice, index);
    }

    private static void DeclareDoorField(IntPtr choice, string field, bool required, string[] dependencies)
    {
        var declaration = new DoorFieldDeclaration("k-lani-bot-form-field/1", field,
            DoorModel.ContextRevision.ToString(CultureInfo.InvariantCulture), required, false, dependencies);

        atk_object_set_description(gtk_widget_get_accessible(choice), JsonSerializer.Serialize(declaration));
    }

    private static void ChangeDoorVariant()
    {
        int index = gtk_combo_box_get_active(doorVariant);
        var options = Enum.GetValues<DoorVariant>();

        if (index >= 0 && index < options.Length && DoorModel.TrySelectModel(options[index], DoorModel.ContextRevision))
        {
            RefreshDoor();
        }
    }

    private static void ChangeDoorWidth()
    {
        int index = gtk_combo_box_get_active(doorWidth);
        var options = DoorModel.WidthOptions;

        if (index >= 0 && index < options.Length && DoorModel.TrySelectWidth(options[index], DoorModel.ContextRevision))
        {
            RefreshDoor();
        }
    }

    private static void ChangeDoorPanels()
    {
        int index = gtk_combo_box_get_active(doorPanels);
        var options = DoorModel.SidePanelOptions;

        if (index >= 0 && index < options.Length && DoorModel.TrySelectSidePanels(options[index], DoorModel.ContextRevision))
        {
            RefreshDoor();
        }
    }

    private static void ChangeDoorGlass()
    {
        int index = gtk_combo_box_get_active(doorGlass);
        var options = DoorModel.GlassOptions;

        if (index >= 0 && index < options.Length && DoorModel.TrySelectGlass(options[index], DoorModel.ContextRevision))
        {
            RefreshDoor();
        }
    }

    // Throws IOException or UnauthorizedAccessException when the private application output cannot be saved.
    private static void SaveDoor(string resultPath)
    {
        if (!DoorModel.TryValidate(out var result))
        {
            gtk_label_set_text(doorStatus, "Missing required or invalid door values");
            return;
        }

        File.WriteAllText(resultPath, JsonSerializer.Serialize(result, ResultOptions));
        gtk_label_set_text(doorStatus, "Saved valid synthetic door");
    }

    [DllImport(Gtk)] private static extern void gtk_widget_set_sensitive(IntPtr widget, int sensitive);
    [DllImport("libatk-1.0.so.0")] private static extern void atk_object_set_accessible_id(IntPtr accessible, string identifier);
    [DllImport("libatk-1.0.so.0")] private static extern void atk_object_set_description(IntPtr accessible, string description);
}
