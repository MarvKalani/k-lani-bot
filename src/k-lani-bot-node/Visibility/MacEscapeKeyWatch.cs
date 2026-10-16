#if !WINDOWS
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace KLaniBot.Visibility;

// Use case: the macOS indicator reports a physical ESC. An event tap runs on a thread with its own run loop, so a busy
// indicator thread never holds up the person's keyboard, and it passes every event on unchanged. A key from the keyboard
// carries the HID system source state; keys that programs post, the node's own among them, do not. That a real key
// reports the HID state still needs one check at a Mac. Without the accessibility permission the tap is not created.
public sealed class MacEscapeKeyWatch : IEscapeKeyWatch
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint SessionEventTap = 1;
    private const uint HeadInsertEventTap = 0;
    private const uint TapOptionDefault = 0;
    private const uint KeyDownEvent = 10;
    private const uint TapDisabledByTimeout = 0xFFFFFFFE;
    private const uint TapDisabledByUserInput = 0xFFFFFFFF;
    private const int KeycodeField = 9;
    private const int SourceStateField = 45;
    private const long HidSystemState = 1;
    private const long EscapeKeycode = 53;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    private readonly ManualResetEventSlim _started = new();
    private readonly Thread _thread;
    private readonly TapCallback _callback;
    private IntPtr _tap;
    private IntPtr _runLoop;
    private int _pressed;

    private MacEscapeKeyWatch()
    {
        _callback = OnEvent;
        _thread = new Thread(Listen) { IsBackground = true, Name = "k-lani-bot escape key" };
    }

    public static bool TryStart([NotNullWhen(true)] out MacEscapeKeyWatch? watch)
    {
        var started = new MacEscapeKeyWatch();
        started._thread.Start();
        bool tapped = started._started.Wait(StartTimeout) && started._tap != IntPtr.Zero;

        if (!tapped)
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
        if (_runLoop != IntPtr.Zero)
        {
            CFRunLoopStop(_runLoop);
        }
        _thread.Join(StartTimeout);
        _started.Dispose();
    }

    private void Listen()
    {
        ulong keyDowns = 1UL << (int)KeyDownEvent;
        _tap = CGEventTapCreate(SessionEventTap, HeadInsertEventTap, TapOptionDefault, keyDowns, _callback, IntPtr.Zero);
        if (_tap == IntPtr.Zero)
        {
            _started.Set();
            return;
        }
        IntPtr source = CFMachPortCreateRunLoopSource(IntPtr.Zero, _tap, 0);
        _runLoop = CFRunLoopGetCurrent();
        CFRunLoopAddSource(_runLoop, source, CommonModes());
        CGEventTapEnable(_tap, true);
        _started.Set();
        CFRunLoopRun();
        CGEventTapEnable(_tap, false);
        CFRelease(source);
        CFRelease(_tap);
    }

    private IntPtr OnEvent(IntPtr proxy, uint eventType, IntPtr keyEvent, IntPtr context)
    {
        if (eventType is TapDisabledByTimeout or TapDisabledByUserInput)
        {
            CGEventTapEnable(_tap, true);
            return keyEvent;
        }
        bool physicalEscape = eventType == KeyDownEvent
            && CGEventGetIntegerValueField(keyEvent, KeycodeField) == EscapeKeycode
            && CGEventGetIntegerValueField(keyEvent, SourceStateField) == HidSystemState;
        if (physicalEscape)
        {
            Interlocked.Exchange(ref _pressed, 1);
        }
        return keyEvent;
    }

    private static IntPtr CommonModes()
    {
        IntPtr library = NativeLibrary.Load(CoreFoundation);
        return Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "kCFRunLoopCommonModes"));
    }

    // CGEventTapCallBack.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr TapCallback(IntPtr proxy, uint eventType, IntPtr keyEvent, IntPtr context);

    [DllImport(CoreGraphics)]
    private static extern IntPtr CGEventTapCreate(
        uint tap,
        uint place,
        uint options,
        ulong eventsOfInterest,
        TapCallback callback,
        IntPtr context);
    [DllImport(CoreGraphics)] private static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.I1)] bool enable);
    [DllImport(CoreGraphics)] private static extern long CGEventGetIntegerValueField(IntPtr keyEvent, int field);
    [DllImport(CoreFoundation)] private static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, long order);
    [DllImport(CoreFoundation)] private static extern IntPtr CFRunLoopGetCurrent();
    [DllImport(CoreFoundation)] private static extern void CFRunLoopAddSource(IntPtr runLoop, IntPtr source, IntPtr mode);
    [DllImport(CoreFoundation)] private static extern void CFRunLoopRun();
    [DllImport(CoreFoundation)] private static extern void CFRunLoopStop(IntPtr runLoop);
    [DllImport(CoreFoundation)] private static extern void CFRelease(IntPtr value);
}
#endif
