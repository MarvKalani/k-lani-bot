#if WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KLaniBot.Platforms.Windows;

// The Windows backend reads here which session and desktops the actuator runs in, so input is refused on a locked or disconnected desktop.
internal static class WindowsDesktopSession
{
    // The connection state of a Windows session as WTS reports it.
    private enum ConnectionState
    {
        Unknown = -1,
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server,
        int session,
        int informationClass,
        out IntPtr buffer,
        out int bytes);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr buffer);
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("user32.dll")]
    private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int bytes, out int needed);

    public static DesktopSessionInfo Read()
    {
        using var process = Process.GetCurrentProcess();
        int session = process.SessionId;
        uint console = WTSGetActiveConsoleSessionId();
        ConnectionState state = ReadConnectionState(session);
        string? workerDesktop = ReadDesktopName(GetThreadDesktop(NativeMethods.GetCurrentThreadId()));
        IntPtr desktop = OpenInputDesktop(0, false, 1);
        try
        {
            string? inputDesktop = ReadDesktopName(desktop);
            // OpenInputDesktop can succeed in a disconnected RDP session. UIA
            // can still read its buffers, so neither proves a usable input screen.
            bool available = session > 0 && state == ConnectionState.Active
                && workerDesktop != null && string.Equals(workerDesktop, inputDesktop, StringComparison.Ordinal);
            return new DesktopSessionInfo(session, console == uint.MaxValue ? null : console,
                state.ToString(), workerDesktop, inputDesktop, available);
        }
        finally
        {
            if (desktop != IntPtr.Zero)
            {
                CloseDesktop(desktop);
            }
        }
    }

    private static ConnectionState ReadConnectionState(int session)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, session, 8, out IntPtr buffer, out int bytes))
        {
            return ConnectionState.Unknown;
        }
        try
        {
            if (buffer == IntPtr.Zero || bytes < sizeof(int))
            {
                return ConnectionState.Unknown;
            }
            int value = Marshal.ReadInt32(buffer);
            return Enum.IsDefined(typeof(ConnectionState), value) ? (ConnectionState)value : ConnectionState.Unknown;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    private static string? ReadDesktopName(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }
        var name = new StringBuilder(256);
        return GetUserObjectInformation(handle, 2, name, name.Capacity * sizeof(char), out _) ? name.ToString() : null;
    }
}
#endif
