using System.Runtime.InteropServices;
using System.Text;

namespace KLaniBot.Platforms.Windows;

// A rectangle as Win32 passes it.
[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

// A point as Win32 passes it.
[StructLayout(LayoutKind.Sequential)]
public struct POINT
{
    public int X;
    public int Y;
}

// A window's show state and normal position as GetWindowPlacement fills it.
[StructLayout(LayoutKind.Sequential)]
public struct WINDOWPLACEMENT
{
    public int length;
    public int flags;
    public int showCmd;
    public POINT ptMinPosition;
    public POINT ptMaxPosition;
    public RECT rcNormalPosition;
}

// The Win32 calls the Windows backend uses for windows, input and the clipboard, in one place.
public static class NativeMethods
{
    // Called by EnumWindows for each top-level window.
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowEnabled(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    // Shares the input queue of two threads, which lets SetForegroundWindow succeed for a window of another application.
    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new IntPtr(-4);

    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const int SW_SHOWMINIMIZED = 2;
    public const int SW_MAXIMIZE = 3;
    public const int SW_RESTORE = 9;

    // Walks parent windows only. An owned dialog stays itself instead of resolving to its owner, which may be disabled.
    public const uint GA_ROOT = 2;

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;

    public const int SM_CXSCREEN = 0;
    public const int SM_CYSCREEN = 1;

    // Win32 Messages for Silent Click & Direct Control Manipulation
    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(
        IntPtr hwnd,
        uint message,
        IntPtr wparam,
        IntPtr lparam,
        uint flags,
        uint timeout,
        out UIntPtr result);

    public const uint BM_CLICK = 0x00F5;

    // Win32 Native Clipboard APIs (STA-thread-free)
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GlobalFree(IntPtr hMem);

    public const uint CF_UNICODETEXT = 13;
    public const uint GMEM_MOVEABLE = 0x0002;

    public static string? GetClipboardText()
    {
        for (int i = 0; i < 10; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    if (!IsClipboardFormatAvailable(CF_UNICODETEXT))
                        return null;

                    IntPtr hMem = GetClipboardData(CF_UNICODETEXT);
                    if (hMem == IntPtr.Zero)
                        return null;

                    IntPtr pText = GlobalLock(hMem);
                    if (pText == IntPtr.Zero)
                        return null;

                    try
                    {
                        return Marshal.PtrToStringUni(pText);
                    }
                    finally
                    {
                        GlobalUnlock(hMem);
                    }
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(20);
        }
        return null;
    }

    private const int ClipboardAttempts = 10;

    // Another program may hold the clipboard open for a moment, so opening it is retried briefly.
    public static bool SetClipboardText(string text)
    {
        if (text == null)
            text = string.Empty;

        for (int i = 0; i < ClipboardAttempts; i++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    return PutUnicodeText(text);
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(20);
        }
        return false;
    }

    // The clipboard is open; the memory belongs to the clipboard once SetClipboardData accepted it.
    private static bool PutUnicodeText(string text)
    {
        int byteCount = (text.Length + 1) * 2;
        IntPtr hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)byteCount);
        if (hMem == IntPtr.Zero)
            return false;
        IntPtr pTarget = GlobalLock(hMem);
        if (pTarget == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return false;
        }
        try
        {
            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            Marshal.Copy(bytes, 0, pTarget, bytes.Length);
        }
        finally
        {
            GlobalUnlock(hMem);
        }

        if (SetClipboardData(CF_UNICODETEXT, hMem) == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return false;
        }
        return true;
    }
}
