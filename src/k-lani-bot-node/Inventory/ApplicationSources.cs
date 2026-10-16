using KLaniBot.Platforms.Linux;
using KLaniBot.Platforms.MacOS;
using KLaniBot.Platforms.Windows;

namespace KLaniBot.Inventory;

// The inventory and the app scan get the source of installed programs for the running system here.
public static class ApplicationSources
{
    // Throws PlatformNotSupportedException outside Windows, macOS and Linux.
    public static IApplicationSource ForCurrentSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsApplicationSource();
        }
        if (OperatingSystem.IsMacOS())
        {
            return new MacApplicationSource();
        }
        if (OperatingSystem.IsLinux())
        {
            return new LinuxApplicationSource();
        }
        throw new PlatformNotSupportedException("The application scanner supports Windows, macOS and Linux.");
    }
}
