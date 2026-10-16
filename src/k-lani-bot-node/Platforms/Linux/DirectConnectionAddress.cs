#if !WINDOWS
namespace KLaniBot.Platforms.Linux;

// AtSpiBackend asks it before it connects to the address an application announces for its own server
// (org.a11y.atspi.Application.GetApplicationBusAddress). Only unix sockets are accepted: other D-Bus transports,
// such as unixexec:, start a process, and an address list could make the choice ambiguous.
public static class DirectConnectionAddress
{
    // libdbus announces a path socket, or an abstract socket where the system supports them.
    private static readonly string[] AcceptedPrefixes = ["unix:path=", "unix:abstract="];

    public static bool IsAccepted(string? address)
    {
        if (string.IsNullOrEmpty(address) || address.Contains(';'))
        {
            return false;
        }

        return AcceptedPrefixes.Any(prefix => address.StartsWith(prefix, StringComparison.Ordinal));
    }
}
#endif
