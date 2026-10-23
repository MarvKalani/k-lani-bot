namespace KLaniBot.ControllerTray;

// Use case: the native part of the controller tray on one operating system draws the menu ControllerTrayMenu computed and
// reports the command the person chose. It is not the node's visibility host: no border, no consent window, no input.
// The service calls Show from a background thread; each host moves the work onto its own UI thread.
internal abstract class ControllerTrayHost : IDisposable
{
    public event Action<ControllerTrayCommand>? Chosen;

    // Draws the view; safe to call from any thread.
    public abstract void Show(ControllerTrayView view);

    // Runs the UI loop on the calling thread until Close.
    public abstract void Run();

    // Ends Run; safe to call from any thread.
    public abstract void Close();

    public abstract void Dispose();

    protected void Choose(ControllerTrayCommand command)
    {
        Chosen?.Invoke(command);
    }

    // Throws PlatformNotSupportedException on macOS, where no controller tray exists yet, and InvalidOperationException when
    // the desktop offers no tray.
    public static ControllerTrayHost Create()
    {
#if WINDOWS
        return new WindowsControllerTray();
#else
        if (OperatingSystem.IsLinux())
        {
            return new LinuxControllerTray();
        }
        throw new PlatformNotSupportedException(
            "The controller tray is implemented for Linux and Windows; on macOS use --master-admin --command controller-status.");
#endif
    }
}
