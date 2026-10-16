namespace KLaniBot.Backup;

// Fixed trusted roots and single-component restore names keep archive and recovery work outside the source being protected.
internal static class BackupPaths
{
    private static readonly StringComparison Comparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$"
    };

    // Throws BackupFault for overlapping or linked roots; path parsing can throw ArgumentException or IOException.
    public static BackupLocations Normalize(BackupLocations locations)
    {
        var normalized = new BackupLocations(Path.GetFullPath(locations.Source), Path.GetFullPath(locations.Archives),
            Path.GetFullPath(locations.Restores));
        string[] roots = [normalized.Source, normalized.Archives, normalized.Restores];
        foreach (string root in roots)
        {
            if (Same(root, Path.GetPathRoot(root)!))
            {
                throw new BackupFault(BackupFailure.InvalidScope);
            }
            RejectLinks(root);
        }
        for (int first = 0; first < roots.Length; first++)
        {
            for (int second = first + 1; second < roots.Length; second++)
            {
                if (Contains(roots[first], roots[second]) || Contains(roots[second], roots[first]))
                {
                    throw new BackupFault(BackupFailure.InvalidScope);
                }
            }
        }

        return normalized;
    }

    public static bool Same(string first, string second)
    {
        return string.Equals(Path.TrimEndingDirectorySeparator(first), Path.TrimEndingDirectorySeparator(second), Comparison);
    }

    private static bool Contains(string parent, string child)
    {
        string prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;

        return Same(parent, child) || child.StartsWith(prefix, Comparison);
    }

    // Throws BackupFault for links, including an existing linked ancestor of a new destination.
    public static void RejectLinks(string path)
    {
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new BackupFault(BackupFailure.InvalidScope);
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static bool ValidRelativeFile(string value)
    {
        return value.Length is > 0 and <= 512 && !Path.IsPathRooted(value) && !value.Contains('\\')
            && !value.Contains(':') && !value.Any(char.IsControl)
            && value.Split('/').All(ValidComponent);
    }

    public static bool ValidDestination(string value)
    {
        return value.Length is > 0 and <= 80 && char.IsAsciiLetterOrDigit(value[0])
            && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.') && ValidComponent(value);
    }

    private static bool ValidComponent(string value)
    {
        if (value.Length == 0 || value is "." or ".." || value.EndsWith('.') || value.EndsWith(' ')
            || value.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
        {
            return false;
        }
        string stem = value.Split('.')[0];
        bool numberedDevice = stem.Length == 4 && stem[3] is >= '1' and <= '9'
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));

        return !DeviceNames.Contains(stem) && !numberedDevice;
    }
}
