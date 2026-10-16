using System.Diagnostics;

namespace KLaniBot.Transport;

// Which platform rules apply to the actuator's environment.
public enum WorkerPlatform
{
    Windows,
    Unix
}

// The supervisor starts the actuator with only the environment it needs, so secrets of the supervisor's environment are not inherited.
public static class WorkerEnvironment
{
    private const string BundleExtractionVariable = "DOTNET_BUNDLE_EXTRACT_BASE_DIR";
    private static readonly string[] SharedVariables =
    [
        "PATH", "LANG", "LC_ALL", "DISPLAY", "WAYLAND_DISPLAY",
        "DBUS_SESSION_BUS_ADDRESS", "XAUTHORITY", BundleExtractionVariable, "SystemRoot"
    ];
    private static readonly string[] WindowsProfileVariables =
    [
        "TEMP", "TMP", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "WINDIR"
    ];

    public static void Configure(ProcessStartInfo start, string privateStateRoot, WorkerPlatform platform)
    {
        var inherited = new Dictionary<string, string?>(start.Environment,
            platform == WorkerPlatform.Windows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        start.Environment.Clear();
        CopyVariables(SharedVariables);
        if (platform != WorkerPlatform.Windows)
        {
            return;
        }
        CopyVariables(WindowsProfileVariables);
        if (!start.Environment.ContainsKey("WINDIR") && start.Environment.TryGetValue("SystemRoot", out string? windowsRoot))
        {
            // WPF's font cache builds an absolute Fonts URI from WINDIR. Older sanitized launchers retained only SystemRoot.
            start.Environment["WINDIR"] = windowsRoot;
        }
        if (start.Environment.TryGetValue(BundleExtractionVariable, out string? configured)
            && !string.IsNullOrWhiteSpace(configured))
        {
            return;
        }

        // The native bundle host extracts files before managed Main can run.
        // Missing TEMP makes a sanitized Windows child fall back to SystemRoot;
        // use the already protected node state, outside the agent workspace.
        string cacheDirectory = Path.Combine(Path.GetFullPath(privateStateRoot), "bundle-cache");
        RejectLinks(cacheDirectory);
        Directory.CreateDirectory(cacheDirectory);
        start.Environment[BundleExtractionVariable] = cacheDirectory;

        void CopyVariables(IEnumerable<string> names)
        {
            foreach (string name in names)
            {
                if (inherited.TryGetValue(name, out string? value) && value != null)
                {
                    start.Environment[name] = value;
                }
            }
        }
    }

    // Throws UnauthorizedAccessException when a part of the path is a link.
    private static void RejectLinks(string path)
    {
        for (string? component = path; component != null; component = Path.GetDirectoryName(component))
        {
            if ((File.Exists(component) || Directory.Exists(component))
                && (File.GetAttributes(component) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Worker bundle cache must not follow links.");
            }
        }
    }
}
