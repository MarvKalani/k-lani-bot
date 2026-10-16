#if WINDOWS
using System.Windows;

namespace KLaniBot.Visibility;

// The native worker owns a WPF application on its STA thread, including resource resolution and dispatcher lifetime.
// Closing one overlay or consent window must not shut down the resident node.
internal static class WindowsWpfRuntime
{
    public static void Initialize()
    {
        if (Application.Current == null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }
    }
}
#endif
