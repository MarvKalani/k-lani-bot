using System.Runtime.InteropServices;
using System.Text.Json;

namespace KLaniBot.CartridgeLab;

// Private accessibility tests and model trials use a scrollable inventory to discover real native controls in bounded pages.
internal static partial class GtkFixture
{
    private const int DiscoveryControlCount = 250;
    private const int DiscoveryWindowWidth = 640;
    private const int DiscoveryWindowHeight = 600;
    private static readonly Dictionary<string, IntPtr> DiscoveryFields = [];

    // Throws InvalidOperationException when a private GTK display is unavailable.
    public static int RunDiscovery(string resultPath)
    {
        if (gtk_init_check(IntPtr.Zero, IntPtr.Zero) == 0)
        {
            throw new InvalidOperationException("GTK3 display required.");
        }

        var window = Window("k-lani-bot · Synthetic discovery", DiscoveryWindowWidth, DiscoveryWindowHeight, out var box);
        Label(box, "SYNTHETIC · 250 native editable controls; no network actions");
        var scroll = gtk_scrolled_window_new(IntPtr.Zero, IntPtr.Zero);
        var fields = gtk_box_new(1, 8);
        gtk_container_add(scroll, fields);
        gtk_box_pack_start(box, scroll, 1, 1, 0);
        AddDiscoveryFields(fields);
        Button(box, "Save observed fields", () => SaveDiscoveryFields(resultPath));
        Hook(window, "destroy", (_, _) => gtk_main_quit());
        gtk_widget_show_all(window);
        gtk_main();

        return 0;
    }

    private static void AddDiscoveryFields(IntPtr box)
    {
        DiscoveryFields.Clear();

        for (int index = 1; index <= DiscoveryControlCount; index++)
        {
            string name = "Field " + index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            var field = gtk_entry_new();
            Name(field, name);
            gtk_entry_set_text(field, "Initial " + name);
            gtk_box_pack_start(box, field, 0, 0, 0);
            DiscoveryFields.Add(name, field);
        }
    }

    // Throws IOException or UnauthorizedAccessException when the private fixture output cannot be saved.
    private static void SaveDiscoveryFields(string resultPath)
    {
        var values = new Dictionary<string, string>();

        foreach (var field in DiscoveryFields)
        {
            values.Add(field.Key, Text(field.Value));
        }

        File.WriteAllText(resultPath, JsonSerializer.Serialize(values));
    }

    [DllImport(Gtk)] private static extern IntPtr gtk_scrolled_window_new(IntPtr horizontalAdjustment, IntPtr verticalAdjustment);
}
