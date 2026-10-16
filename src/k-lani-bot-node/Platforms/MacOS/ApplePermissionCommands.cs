using System.Text.Json;
using KLaniBot.Cartridges;
using KLaniBot.Common;
using KLaniBot.Startup;

namespace KLaniBot.Platforms.MacOS;

// macOS grants accessibility and screen recording per application and only after the person at the
// Mac confirms it. These commands exist for onboarding and for diagnosing a node that cannot act.
internal static class ApplePermissionCommands
{
    // The macOS installer starts this once in the person's GUI session so that the system dialogs
    // appear; the optional report file tells the installer what was granted.
    // Throws PlatformNotSupportedException outside macOS.
    public static int Request(NodeArguments arguments)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Apple onboarding requires macOS.");
        }
        var result = MacUiService.RequestPermissions();
        string json = JsonSerializer.Serialize(result);
        if (arguments.Option("--permission-report") is { } reportPath)
        {
            reportPath = Path.GetFullPath(reportPath);
            CartridgeManifest.RejectLinks(reportPath);
            PrivateFile.WriteAllText(reportPath, json, replace: true);
        }
        Console.WriteLine(json);
        return 0;
    }

    // An operator runs this on the Mac to see the current grants without triggering a dialog.
    // Throws PlatformNotSupportedException outside macOS.
    public static int Show()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Apple permission diagnostics require macOS.");
        }
        Console.WriteLine(JsonSerializer.Serialize(MacUiService.Permissions()));
        return 0;
    }
}
