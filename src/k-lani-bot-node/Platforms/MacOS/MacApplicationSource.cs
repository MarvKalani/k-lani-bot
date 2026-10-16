using KLaniBot.Common;
using KLaniBot.Inventory;

namespace KLaniBot.Platforms.MacOS;

// Use case: list the application bundles of a Mac. The bundle folders in the application directories are
// the standard place; name, version and bundle identifier come from each bundle's Info.plist, read with
// the system tool plutil so that binary property lists work too. Nothing is started.
public sealed class MacApplicationSource : IApplicationSource
{
    private const string Plutil = "/usr/bin/plutil";

    public string OsFamily => "macos";

    public IReadOnlyList<string> Limitations =>
    [
        "App bundles in /Applications, /Applications/Utilities and ~/Applications are listed. Programs elsewhere and "
        + "command-line tools are missing."
    ];

    public IReadOnlyList<ApplicationEvidence> Enumerate(IReadOnlyCollection<string> executableHints)
    {
        var evidence = new List<ApplicationEvidence>();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] applicationRoots = ["/Applications", "/Applications/Utilities", Path.Combine(home, "Applications")];
        foreach (string directory in applicationRoots.Where(Directory.Exists))
        {
            foreach (string bundle in Directory.EnumerateDirectories(directory, "*.app").Order(StringComparer.Ordinal))
            {
                evidence.Add(Describe(bundle));
            }
        }
        return evidence;
    }

    private static ApplicationEvidence Describe(string bundle)
    {
        string folder = Path.GetFileName(bundle);
        string name = Path.GetFileNameWithoutExtension(folder);
        string plist = Path.Combine(bundle, "Contents", "Info.plist");
        string version = File.Exists(plist) ? ReadKey(plist, "CFBundleShortVersionString") : "";
        string bundleId = File.Exists(plist) ? ReadKey(plist, "CFBundleIdentifier") : "";
        var identifiers = new[] { folder, name, bundleId }.Where(item => item.Length > 0).ToArray();
        return new ApplicationEvidence(name, version, "", "bundle", identifiers, ApplicationKind.Application, bundle);
    }

    private static string ReadKey(string plist, string key)
    {
        var result = ProcessRunner.Run(Plutil, ["-extract", key, "raw", "-o", "-", plist], 5000);
        return result.Success ? result.Output.Trim() : "";
    }
}
