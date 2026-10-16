using System.Globalization;

namespace KLaniBot.Platforms.Linux;

// One window of `wmctrl -l -p -G`: its X11 handle, owning process and title, which is empty for an untitled dialog.
public sealed record WmctrlWindow(long Handle, int ProcessId, string Title);

// LinuxUiService reads each output line of `wmctrl -l -p -G` here: id, desktop, pid, x, y, width, height, host, title.
// A window without a title prints only the eight fields before it.
public static class WmctrlLineParser
{
    private const int FieldsBeforeTitle = 8;
    private const int ProcessIdField = 2;

    public static bool TryParse(string line, out WmctrlWindow window)
    {
        window = new WmctrlWindow(0, 0, "");
        string[] fields = line.Split(' ', FieldsBeforeTitle + 1, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < FieldsBeforeTitle)
        {
            return false;
        }

        string handleText = fields[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? fields[0][2..] : fields[0];
        if (!long.TryParse(handleText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long handle))
        {
            return false;
        }
        if (!int.TryParse(fields[ProcessIdField], NumberStyles.Integer, CultureInfo.InvariantCulture, out int processId))
        {
            return false;
        }

        string title = fields.Length > FieldsBeforeTitle ? fields[FieldsBeforeTitle].TrimStart() : "";
        window = new WmctrlWindow(handle, processId, title);

        return true;
    }
}
