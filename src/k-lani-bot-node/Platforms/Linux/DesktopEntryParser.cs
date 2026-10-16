namespace KLaniBot.Platforms.Linux;

// The fields of a desktop entry the inventory uses.
public sealed record DesktopEntry(string FileName, string Name, string Exec, string[] Categories, string AppImageVersion);

// Use case: on Linux a program that a person starts from a menu or launcher has a "desktop entry"
// (freedesktop.org specification). Reading these files is the standard, read-only way to list
// the graphical programs of a machine without running any of them.
//
// Only the "Desktop Entry" group matters. Entries that are hidden, not applications, or marked
// NoDisplay are not shown to a person and are skipped.
public static class DesktopEntryParser
{
    public static DesktopEntry? Parse(string fileName, string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        bool inGroup = false;
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                inGroup = line == "[Desktop Entry]";
                continue;
            }
            if (!inGroup || line.StartsWith('#') || line.Length == 0)
            {
                continue;
            }
            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }
            // Keys with a locale, such as Name[de], are translations; the unlocalised Name is the stable one.
            values.TryAdd(line[..separator].Trim(), line[(separator + 1)..].Trim());
        }
        bool isApplication = values.GetValueOrDefault("Type") == "Application";
        bool shown = values.GetValueOrDefault("NoDisplay") != "true" && values.GetValueOrDefault("Hidden") != "true";
        string name = values.GetValueOrDefault("Name", "");
        if (!isApplication || !shown || name.Length == 0)
        {
            return null;
        }
        string[] categories = values.GetValueOrDefault("Categories", "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        return new DesktopEntry(
            fileName,
            name,
            values.GetValueOrDefault("Exec", ""),
            categories,
            values.GetValueOrDefault("X-AppImage-Version", ""));
    }

    // The program a launcher line starts: "env A=1 /usr/bin/prog --flag %U" gives "prog"; "flatpak run org.x.App" gives "org.x.App".
    public static string ExecutableName(string exec)
    {
        string[] words = SplitWords(exec);
        int index = 0;
        while (index < words.Length && (words[index] == "env" || words[index].Contains('=')))
        {
            index++;
        }
        if (index >= words.Length)
        {
            return "";
        }
        string program = Path.GetFileName(words[index]);
        if (program is "flatpak" or "snap")
        {
            string? target = words.Skip(index + 1).FirstOrDefault(word => !word.StartsWith('-') && word != "run");
            return target ?? program;
        }
        return program;
    }

    // The full path of the program a launcher line starts, or "" when it is only a command name found on PATH.
    public static string ExecutablePath(string exec)
    {
        string[] words = SplitWords(exec);
        string? program = words.FirstOrDefault(word => word != "env" && !word.Contains('='));
        return program != null && program.StartsWith('/') ? program : "";
    }

    private static string[] SplitWords(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char character in text)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(character) && !quoted)
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(character);
            }
        }
        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }
        return words.ToArray();
    }
}
