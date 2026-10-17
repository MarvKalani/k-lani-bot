#if !WINDOWS
using System.Runtime.InteropServices;

namespace KLaniBot.Platforms.Linux;

// Use case: the last-resort input route needs a window's rectangle in root pixels, the pixels scrot captures and xdotool clicks.
// xdotool and wmctrl add the frame offset twice under a reparenting window manager, so the origin is the window's own (0, 0)
// translated to the root window. XCB reports a vanished window as a request error, where Xlib's default handler ends the process.
internal static class X11WindowRectangle
{
    private const string Xcb = "libxcb.so.1";
    private const string C = "libc";

    // Throws InvalidOperationException when the display or the window does not answer.
    public static WindowScreenBounds Read(long handle)
    {
        IntPtr connection = xcb_connect(IntPtr.Zero, IntPtr.Zero);
        try
        {
            if (xcb_connection_has_error(connection) != 0)
            {
                throw new InvalidOperationException("The X display refused a connection for the window rectangle.");
            }

            return Read(connection, (uint)handle);
        }
        finally
        {
            xcb_disconnect(connection);
        }
    }

    // Throws InvalidOperationException when the window is gone or the server answers with an error.
    private static WindowScreenBounds Read(IntPtr connection, uint window)
    {
        GeometryReply size = Reply<GeometryReply>(xcb_get_geometry_reply(connection, xcb_get_geometry(connection, window),
            out IntPtr geometryError), geometryError);
        Cookie translation = xcb_translate_coordinates(connection, window, size.Root, 0, 0);
        TranslationReply origin = Reply<TranslationReply>(xcb_translate_coordinates_reply(connection, translation,
            out IntPtr originError), originError);

        return new WindowScreenBounds(origin.X, origin.Y, size.Width, size.Height);
    }

    // Throws InvalidOperationException for a missing reply; both the reply and the error are freed here.
    private static T Reply<T>(IntPtr reply, IntPtr error) where T : struct
    {
        free(error);
        if (reply == IntPtr.Zero)
        {
            throw new InvalidOperationException("The X server reported no rectangle for this window.");
        }
        T value = Marshal.PtrToStructure<T>(reply);
        free(reply);

        return value;
    }

    // xcb_void_cookie_t and its kin: the sequence number of a sent request.
    [StructLayout(LayoutKind.Sequential)]
    private struct Cookie
    {
        public uint Sequence;
    }

    // xcb_get_geometry_reply_t.
    [StructLayout(LayoutKind.Sequential)]
    private struct GeometryReply
    {
        public byte ResponseType;
        public byte Depth;
        public ushort SequenceNumber;
        public uint Length;
        public uint Root;
        public short X;
        public short Y;
        public ushort Width;
        public ushort Height;
        public ushort BorderWidth;
    }

    // xcb_translate_coordinates_reply_t.
    [StructLayout(LayoutKind.Sequential)]
    private struct TranslationReply
    {
        public byte ResponseType;
        public byte SameScreen;
        public ushort SequenceNumber;
        public uint Length;
        public uint Child;
        public short X;
        public short Y;
    }

    [DllImport(Xcb)] private static extern IntPtr xcb_connect(IntPtr displayName, IntPtr screen);
    [DllImport(Xcb)] private static extern int xcb_connection_has_error(IntPtr connection);
    [DllImport(Xcb)] private static extern void xcb_disconnect(IntPtr connection);
    [DllImport(Xcb)] private static extern Cookie xcb_get_geometry(IntPtr connection, uint drawable);
    [DllImport(Xcb)] private static extern IntPtr xcb_get_geometry_reply(IntPtr connection, Cookie cookie, out IntPtr error);
    [DllImport(Xcb)]
    private static extern Cookie xcb_translate_coordinates(IntPtr connection, uint source, uint destination, short x, short y);
    [DllImport(Xcb)] private static extern IntPtr xcb_translate_coordinates_reply(IntPtr connection, Cookie cookie, out IntPtr error);
    [DllImport(C)] private static extern void free(IntPtr pointer);
}
#endif
