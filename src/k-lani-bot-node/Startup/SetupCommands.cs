using KLaniBot.Inventory;
using KLaniBot.Platforms.MacOS;
using KLaniBot.Platforms.Windows;

namespace KLaniBot.Startup;

// Commands that install, update or configure a node on its own machine.
internal static class SetupCommands
{
    // The Windows installer starts this with the invitation file a master issued for this machine.
    // Throws PlatformNotSupportedException outside Windows.
    public static Task<int> EnrollAsync(NodeArguments arguments)
    {
        string invitation = arguments.RequireOption("--enroll");
#if WINDOWS
        return WindowsNodeSetup.RunEnrollmentAsync(invitation);
#else
        throw new PlatformNotSupportedException("The managed desktop installer currently targets Windows.");
#endif
    }

    // The running Windows node starts a copy of the new version with the verified update plan.
    public static Task<int> ApplyUpdateAsync(NodeArguments arguments)
    {
        return WindowsNodeUpdater.ApplyAsync(arguments.RequireOption("--apply-update"));
    }

    // The macOS installation cartridge (a signed package script) installs and configures the node.
    public static Task<int> InstallCartridgeAsync(NodeArguments arguments)
    {
        return MacInstallationCartridge.RunAsync(arguments.Tokens.ToArray());
    }

    // An operator issues the node credentials and the worker policy once, before the first --serve.
    public static async Task<int> InitAsync(NodeArguments arguments)
    {
        await NodeInitializer.InitializeAsync(arguments.Tokens.ToArray());
        return 0;
    }

    // An operator or the master asks which installed applications the cartridges know.
    public static int ScanApps(NodeArguments arguments)
    {
        return AppScanCommand.Run(arguments.Tokens.ToArray(), Console.Out);
    }
}
