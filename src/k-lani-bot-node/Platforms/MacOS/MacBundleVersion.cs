using System.Collections.Concurrent;
using KLaniBot.Common;

namespace KLaniBot.Platforms.MacOS;

// Use case: the window list names the version of a running application, read from the Info.plist of the bundle that
// holds its executable. A program outside a bundle or an unreadable plist stays "unknown"; nothing is guessed.
public static class MacBundleVersion
{
    public const string Unknown = "unknown";
    private const string Plutil = "/usr/bin/plutil";
    private const string BundleMarker = ".app/Contents/";
    private const int MaximumVersionLength = 64;
    private const int PlutilTimeoutMilliseconds = 5000;
    private static readonly ConcurrentDictionary<string, string> Versions = new();

    // The directory of the innermost .app bundle that contains the executable, or null for a program outside a bundle.
    public static string? BundleRoot(string? executablePath)
    {
        if (executablePath == null)
        {
            return null;
        }
        int marker = executablePath.LastIndexOf(BundleMarker, StringComparison.Ordinal);

        return marker < 0 ? null : executablePath[..(marker + ".app".Length)];
    }

    public static string Read(string? executablePath)
    {
        string? bundle = BundleRoot(executablePath);
        if (bundle == null)
        {
            return Unknown;
        }

        return Versions.GetOrAdd(bundle, ReadVersion);
    }

    private static string ReadVersion(string bundle)
    {
        string plist = Path.Combine(bundle, "Contents", "Info.plist");
        if (!File.Exists(plist))
        {
            return Unknown;
        }
        var result = ProcessRunner.Run(Plutil, ["-extract", "CFBundleShortVersionString", "raw", "-o", "-", plist],
            PlutilTimeoutMilliseconds);
        string version = result.Success ? result.Output.Trim() : "";

        return version.Length is > 0 and <= MaximumVersionLength ? version : Unknown;
    }
}
