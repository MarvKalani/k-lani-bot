#if !WINDOWS
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace KLaniBot.Visibility;

// Use case: the Linux indicator under X11 reports a physical ESC. It reads XInput 2 raw key events on a display connection
// of its own, which never takes the key from the focused program. xdotool, which the node types with, sends through the
// XTEST device, and events from that device are skipped. Wayland has no red border, so there is nothing to watch there.
public sealed class X11EscapeKeyWatch : IEscapeKeyWatch
{
    private const string X11 = "libX11.so.6";
    private const string Xi = "libXi.so.6";
    private const int GenericEvent = 35;
    private const int RawKeyPress = 13;
    private const int AllDevices = 0;
    private const int Success = 0;
    private const ulong EscapeKeysym = 0xff1b;
    private const int EventBytes = 192;

    // Offsets in XGenericEventCookie, XIRawEvent and XIDeviceInfo on 64-bit Linux.
    private const int CookieExtensionOffset = 32;
    private const int CookieEventTypeOffset = 36;
    private const int CookieDataOffset = 48;
    private const int RawSourceOffset = 52;
    private const int RawDetailOffset = 56;
    private const int DeviceInfoBytes = 40;
    private const int DeviceNameOffset = 8;

    private readonly IntPtr _display;
    private readonly int _opcode;
    private readonly int _escapeKeycode;
    private readonly HashSet<int> _syntheticDevices;
    private readonly bool _reportSyntheticKeys;
    private readonly IntPtr _event = Marshal.AllocHGlobal(EventBytes);

    private X11EscapeKeyWatch(IntPtr display, int opcode, bool reportSyntheticKeys)
    {
        _display = display;
        _opcode = opcode;
        _reportSyntheticKeys = reportSyntheticKeys;
        _escapeKeycode = XKeysymToKeycode(display, EscapeKeysym);
        _syntheticDevices = SyntheticDevices(display);
        SelectRawKeyPresses(display);
    }

    // reportSyntheticKeys is for the test in a virtual display, which has no physical keyboard to press.
    public static bool TryStart(bool reportSyntheticKeys, [NotNullWhen(true)] out X11EscapeKeyWatch? watch)
    {
        watch = null;
        IntPtr display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero)
        {
            return false;
        }

        int major = 2;
        int minor = 0;
        bool hasInput2 = XQueryExtension(display, "XInputExtension", out int opcode, out _, out _) != 0
            && XIQueryVersion(display, ref major, ref minor) == Success;
        if (!hasInput2)
        {
            XCloseDisplay(display);
            return false;
        }
        watch = new X11EscapeKeyWatch(display, opcode, reportSyntheticKeys);
        return true;
    }

    public bool TakePhysicalEscape()
    {
        bool pressed = false;
        while (XPending(_display) > 0)
        {
            XNextEvent(_display, _event);
            pressed |= IsPhysicalEscape();
        }
        return pressed;
    }

    public void Dispose()
    {
        XCloseDisplay(_display);
        Marshal.FreeHGlobal(_event);
    }

    private bool IsPhysicalEscape()
    {
        bool rawKeyPress = Marshal.ReadInt32(_event) == GenericEvent
            && Marshal.ReadInt32(_event, CookieExtensionOffset) == _opcode
            && Marshal.ReadInt32(_event, CookieEventTypeOffset) == RawKeyPress;
        if (!rawKeyPress || XGetEventData(_display, _event) == 0)
        {
            return false;
        }
        try
        {
            IntPtr raw = Marshal.ReadIntPtr(_event, CookieDataOffset);
            int source = Marshal.ReadInt32(raw, RawSourceOffset);
            bool escape = Marshal.ReadInt32(raw, RawDetailOffset) == _escapeKeycode;
            return escape && (_reportSyntheticKeys || !_syntheticDevices.Contains(source));
        }
        finally
        {
            XFreeEventData(_display, _event);
        }
    }

    // The XTEST devices carry keys that programs send, among them the node's own through xdotool.
    private static HashSet<int> SyntheticDevices(IntPtr display)
    {
        var synthetic = new HashSet<int>();
        IntPtr devices = XIQueryDevice(display, AllDevices, out int count);
        try
        {
            for (int index = 0; index < count; index++)
            {
                IntPtr device = devices + index * DeviceInfoBytes;
                string name = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(device, DeviceNameOffset)) ?? "";
                if (name.Contains("XTEST", StringComparison.Ordinal))
                {
                    synthetic.Add(Marshal.ReadInt32(device));
                }
            }
        }
        finally
        {
            XIFreeDeviceInfo(devices);
        }
        return synthetic;
    }

    private static void SelectRawKeyPresses(IntPtr display)
    {
        byte[] maskBits = new byte[RawKeyPress / 8 + 1];
        maskBits[RawKeyPress / 8] |= (byte)(1 << (RawKeyPress % 8));
        GCHandle pinned = GCHandle.Alloc(maskBits, GCHandleType.Pinned);
        try
        {
            var mask = new EventMask { DeviceId = AllDevices, MaskLength = maskBits.Length, Mask = pinned.AddrOfPinnedObject() };
            XISelectEvents(display, XDefaultRootWindow(display), ref mask, 1);
            XFlush(display);
        }
        finally
        {
            pinned.Free();
        }
    }

    // XIEventMask: which XInput 2 events of which devices a window receives.
    [StructLayout(LayoutKind.Sequential)]
    private struct EventMask
    {
        public int DeviceId;
        public int MaskLength;
        public IntPtr Mask;
    }

    [DllImport(X11)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X11)] private static extern int XCloseDisplay(IntPtr display);
    [DllImport(X11)] private static extern int XFlush(IntPtr display);
    [DllImport(X11)] private static extern int XPending(IntPtr display);
    [DllImport(X11)] private static extern int XNextEvent(IntPtr display, IntPtr eventBuffer);
    [DllImport(X11)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(X11)] private static extern byte XKeysymToKeycode(IntPtr display, ulong keysym);
    [DllImport(X11)] private static extern int XGetEventData(IntPtr display, IntPtr cookie);
    [DllImport(X11)] private static extern void XFreeEventData(IntPtr display, IntPtr cookie);
    [DllImport(X11)]
    private static extern int XQueryExtension(
        IntPtr display,
        [MarshalAs(UnmanagedType.LPStr)] string name,
        out int opcode,
        out int firstEvent,
        out int firstError);
    [DllImport(Xi)] private static extern int XIQueryVersion(IntPtr display, ref int major, ref int minor);
    [DllImport(Xi)] private static extern int XISelectEvents(IntPtr display, IntPtr window, ref EventMask mask, int count);
    [DllImport(Xi)] private static extern IntPtr XIQueryDevice(IntPtr display, int device, out int count);
    [DllImport(Xi)] private static extern void XIFreeDeviceInfo(IntPtr info);
}
#endif
