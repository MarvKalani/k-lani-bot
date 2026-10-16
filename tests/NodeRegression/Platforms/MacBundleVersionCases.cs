using KLaniBot.Platforms.MacOS;

// A running Mac application is named by the bundle that holds its executable, and by nothing else.
// The path logic is pure, so it is checked here; the plist read itself is covered by the Mac acceptance.
internal static class MacBundleVersionCases
{
    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Mac bundle version");
        CheckBundleRoot(checks);
        CheckVersionRead(checks, temporaryRoot);

        return checks.Passed;
    }

    private static void CheckBundleRoot(Checks checks)
    {
        checks.That(MacBundleVersion.BundleRoot("/Applications/TextEdit.app/Contents/MacOS/TextEdit") == "/Applications/TextEdit.app",
            "executable in a bundle names its bundle");
        string nested = "/Applications/Host.app/Contents/Frameworks/Helper.app/Contents/MacOS/Helper";
        checks.That(MacBundleVersion.BundleRoot(nested) == "/Applications/Host.app/Contents/Frameworks/Helper.app",
            "the innermost bundle owns a helper executable");
        checks.That(MacBundleVersion.BundleRoot("/usr/bin/python3") == null, "a program outside a bundle has no bundle");
        checks.That(MacBundleVersion.BundleRoot("/Applications/Tool.app") == null, "a bundle folder itself is not an executable");
        checks.That(MacBundleVersion.BundleRoot(null) == null, "an unreadable path has no bundle");
    }

    private static void CheckVersionRead(Checks checks, string temporaryRoot)
    {
        checks.That(MacBundleVersion.Read(null) == MacBundleVersion.Unknown, "no path is unknown");
        checks.That(MacBundleVersion.Read("/usr/bin/true") == MacBundleVersion.Unknown, "no bundle is unknown");
        string bundle = Path.Combine(temporaryRoot, "mac-bundle-version", "Absent.app");
        checks.That(MacBundleVersion.Read(Path.Combine(bundle, "Contents", "MacOS", "Absent")) == MacBundleVersion.Unknown,
            "a bundle without Info.plist is unknown");
    }
}
