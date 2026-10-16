#if WINDOWS
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace KLaniBot.Visibility;

// Use case: the Windows indicator reports a physical ESC. A low-level keyboard hook runs on a thread of its own, because
// Windows drops a hook whose thread does not answer in time, and the indicator's thread also runs UI Automation calls.
// Keys sent with SendInput, the node's own among them, carry LLKHF_INJECTED and are skipped.
public sealed class WindowsEscapeKeyWatch : IEscapeKeyWatch
{
    private const int LowLevelKeyboardHook = 13;
    private const int KeyDown = 0x0100;
    private const int SystemKeyDown = 0x0104;
    private const int EscapeVirtualKey = 0x1B;
    private const int InjectedFlag = 0x10;
    private const uint QuitMessage = 0x0012;
    private const int FlagsOffset = 8;
    private const int MessageBytes = 64;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    private readonly ManualResetEventSlim _started = new();
    private readonly Thread _thread;
    private readonly HookProcedure _procedure;
    private uint _threadId;
    private IntPtr _hook;
    private int _pressed;

    private WindowsEscapeKeyWatch()
    {
        _procedure = OnKey;
        _thread = new Thread(Listen) { IsBackground = true, Name = "k-lani-bot escape key" };
    }

    public static bool TryStart([NotNullWhen(true)] out WindowsEscapeKeyWatch? watch)
    {
        var started = new WindowsEscapeKeyWatch();
        started._thread.Start();
        bool hooked = started._started.Wait(StartTimeout) && started._hook != IntPtr.Zero;

        if (!hooked)
        {
            started.Dispose();
            watch = null;
            return false;
        }
        watch = started;
        return true;
    }

    public bool TakePhysicalEscape()
    {
        return Interlocked.Exchange(ref _pressed, 0) == 1;
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, QuitMessage, IntPtr.Zero, IntPtr.Zero);
        }
        _thread.Join(StartTimeout);
        _started.Dispose();
    }

    private void Listen()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(LowLevelKeyboardHook, _procedure, GetModuleHandle(null), 0);
        _started.Set();
        if (_hook == IntPtr.Zero)
        {
            return;
        }
        IntPtr message = Marshal.AllocHGlobal(MessageBytes);
        try
        {
            while (GetMessage(message, IntPtr.Zero, 0, 0) > 0)
            {
            }
        }
        finally
        {
            Marshal.FreeHGlobal(message);
            UnhookWindowsHookEx(_hook);
        }
    }

    // Only notes the key: a hook that takes long is dropped by Windows.
    private IntPtr OnKey(int code, IntPtr messageKind, IntPtr keyData)
    {
        bool keyDown = (int)messageKind is KeyDown or SystemKeyDown;
        if (code >= 0 && keyDown && Marshal.ReadInt32(keyData) == EscapeVirtualKey
            && (Marshal.ReadInt32(keyData, FlagsOffset) & InjectedFlag) == 0)
        {
            Interlocked.Exchange(ref _pressed, 1);
        }
        return CallNextHookEx(IntPtr.Zero, code, messageKind, keyData);
    }

    // LowLevelKeyboardProc.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr HookProcedure(int code, IntPtr messageKind, IntPtr keyData);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookKind, HookProcedure procedure, IntPtr module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr messageKind, IntPtr keyData);
    [DllImport("user32.dll")] private static extern int GetMessage(IntPtr message, IntPtr window, uint first, uint last);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? moduleName);
}
#endif
