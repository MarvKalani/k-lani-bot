#if !WINDOWS
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace KLaniBot.Platforms.Linux;

// Semantic actions of the AT-SPI client (Action.DoAction of AT-SPI 2, at-spi2-core 2.x).
//
// at-spi2-atk (GTK3) runs the action inside the dispatch of the connection that delivered it. An action that opens a
// dialog with gtk_dialog_run therefore keeps that connection busy until the dialog closes. Sent on libatspi's own
// connection, it would leave libatspi without a way to read the application. So the action travels on a connection of
// its own. That connection must stay open and silent until the handler has returned: libdbus cannot dispatch it a second
// time, and a message or a disconnect pending on it deadlocks the application on its next key event.
internal sealed partial class AtSpiBackend
{
    private const string Gio = "libgio-2.0.so.0";
    private const string GLibLibrary = "libglib-2.0.so.0";
    private const int ActionTimeoutMilliseconds = 2000;
    // The handler may open its dialog some time after it answered, so a connection is never judged earlier than this.
    private const long ActionHoldMilliseconds = 5000;
    private const long ReleaseCheckIntervalMilliseconds = 1000;
    private const string AccessibilityBusName = "org.a11y.Bus";
    private const string AccessibilityBusPath = "/org/a11y/bus";
    private const string ApplicationRootPath = "/org/a11y/atspi/accessible/root";
    private const string ApplicationInterface = "org.a11y.atspi.Application";
    private const string ActionInterface = "org.a11y.atspi.Action";
    private const string AccessibilityBusAddressVariable = "AT_SPI_BUS_ADDRESS";
    private const string SessionBusAddressVariable = "DBUS_SESSION_BUS_ADDRESS";

    // What the application answered to an action sent on a connection of its own.
    private enum DirectActionOutcome
    {
        // Nothing was sent: the application offers no direct connection or it could not be opened.
        NotAttempted,
        Performed,
        Refused,
        // The request was sent and no answer came, so the action may or may not have run.
        NoAnswer
    }

    // What a semantic action did, whichever connection carried it.
    private enum NativeActionResult
    {
        Performed,
        Refused
    }

    // Values of GDBusConnectionFlags (gio/gioenums.h).
    private enum DirectConnectionKind
    {
        // Peer to peer, no message bus: AUTHENTICATION_CLIENT.
        Peer = 1,
        // A message bus client: AUTHENTICATION_CLIENT | MESSAGE_BUS_CONNECTION.
        Bus = 1 | 8
    }

    // A connection that carried one action, kept open until the application's handler for it has returned.
    private sealed record HeldActionConnection(IntPtr Connection, int ProcessId, long HeldSinceMilliseconds);

    // Where libatspi has an accessible object: the unique bus name of its application and its D-Bus object path.
    private sealed record AccessibleAddress(string BusName, string ObjectPath);

    // Performs the action on the element and says whether the provider performed it.
    // Throws InvalidOperationException when the application does not answer or the provider reports an error.
    private NativeActionResult DoAction(IntPtr element, IntPtr action, int index)
    {
        if (TryReadAddress(element, out AccessibleAddress target))
        {
            DirectActionOutcome outcome = SendActionOnOwnConnection(target, Pid(element), index);
            if (outcome == DirectActionOutcome.NoAnswer)
            {
                throw new InvalidOperationException(
                    "The application did not answer the action request; it may be busy. The action may have run.");
            }
            if (outcome != DirectActionOutcome.NotAttempted)
            {
                return outcome == DirectActionOutcome.Performed ? NativeActionResult.Performed : NativeActionResult.Refused;
            }
        }

        return DoActionThroughLibatspi(action, index);
    }

    // Throws InvalidOperationException when the provider reports an error.
    private static NativeActionResult DoActionThroughLibatspi(IntPtr action, int index)
    {
        IntPtr error = IntPtr.Zero;
        bool performed = atspi_action_do_action(action, index, ref error) != 0;
        Check(error);

        return performed ? NativeActionResult.Performed : NativeActionResult.Refused;
    }

    // Reads the public AtspiObject and AtspiApplication structs (atspi-object.h, atspi-application.h): libatspi has no
    // accessor for either value. A layout that does not yield a valid name and path is reported as unreadable.
    private static bool TryReadAddress(IntPtr element, out AccessibleAddress address)
    {
        int objectHeader = 3 * IntPtr.Size;
        IntPtr application = Marshal.ReadIntPtr(element, objectHeader);
        IntPtr pathPointer = Marshal.ReadIntPtr(element, objectHeader + IntPtr.Size);
        address = new AccessibleAddress("", "");
        if (application == IntPtr.Zero || pathPointer == IntPtr.Zero)
        {
            return false;
        }

        IntPtr busNamePointer = Marshal.ReadIntPtr(application, objectHeader + IntPtr.Size);
        string? busName = busNamePointer == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(busNamePointer);
        string? path = Marshal.PtrToStringUTF8(pathPointer);
        if (busName == null || path == null || g_dbus_is_name(busName) == 0 || g_variant_is_object_path(path) == 0)
        {
            return false;
        }
        address = new AccessibleAddress(busName, path);

        return true;
    }

    // The connection is held, not closed, whatever the answer was: see the type comment.
    private DirectActionOutcome SendActionOnOwnConnection(AccessibleAddress target, int processId, int index)
    {
        IntPtr bus = OpenAccessibilityBus();
        if (bus == IntPtr.Zero)
        {
            return DirectActionOutcome.NotAttempted;
        }

        try
        {
            string? directAddress = ReadDirectAddress(bus, target.BusName);
            IntPtr peer = directAddress == null ? IntPtr.Zero : OpenConnection(directAddress, DirectConnectionKind.Peer);
            if (peer == IntPtr.Zero)
            {
                return DirectActionOutcome.NotAttempted;
            }
            _heldActionConnections.Add(new HeldActionConnection(peer, processId, Environment.TickCount64));

            return CallDoAction(peer, target.ObjectPath, index);
        }
        finally
        {
            CloseConnection(bus);
        }
    }

    // Closes the connections whose handler has returned. A handler that runs a dialog loop has returned once the
    // application shows no modal window any more. gtk_dialog_run makes its dialog modal while it runs.
    private void ReleaseSettledActionConnections()
    {
        long now = Environment.TickCount64;
        if (_heldActionConnections.Count == 0 || now - _lastReleaseCheckMilliseconds < ReleaseCheckIntervalMilliseconds)
        {
            return;
        }
        _lastReleaseCheckMilliseconds = now;

        foreach (HeldActionConnection held in _heldActionConnections.ToArray())
        {
            bool oldEnough = now - held.HeldSinceMilliseconds >= ActionHoldMilliseconds;
            if (oldEnough && !HasShowingModalWindow(held.ProcessId))
            {
                CloseConnection(held.Connection);
                _heldActionConnections.Remove(held);
            }
        }
    }

    // True when the application shows a modal window and also when that cannot be read: a handler is then assumed to
    // still run, because closing its connection too early is the dangerous mistake.
    private bool HasShowingModalWindow(int processId)
    {
        try
        {
            return ShowingModalWindowTitle(processId) != null;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static DirectActionOutcome CallDoAction(IntPtr peer, string objectPath, int index)
    {
        IntPtr[] arguments = [g_variant_new_int32(index)];
        IntPtr parameters = g_variant_new_tuple(arguments, (UIntPtr)arguments.Length);
        IntPtr error = IntPtr.Zero;
        IntPtr reply = g_dbus_connection_call_sync(
            peer,
            null,
            objectPath,
            ActionInterface,
            "DoAction",
            parameters,
            IntPtr.Zero,
            0,
            ActionTimeoutMilliseconds,
            IntPtr.Zero,
            ref error);
        if (error != IntPtr.Zero)
        {
            g_error_free(error);
            return DirectActionOutcome.NoAnswer;
        }

        IntPtr performed = g_variant_get_child_value(reply, UIntPtr.Zero);
        bool accepted = g_variant_get_boolean(performed) != 0;
        g_variant_unref(performed);
        g_variant_unref(reply);

        return accepted ? DirectActionOutcome.Performed : DirectActionOutcome.Refused;
    }

    // The unix socket of the application's own server, or null when it offers none.
    private static string? ReadDirectAddress(IntPtr bus, string busName)
    {
        string? address = ReadStringReply(bus, busName, ApplicationRootPath, ApplicationInterface, "GetApplicationBusAddress");

        return DirectConnectionAddress.IsAccepted(address) ? address : null;
    }

    // The accessibility bus is its own bus daemon; libatspi finds it the same way (AT_SPI_BUS_ADDRESS, else the session bus).
    private static IntPtr OpenAccessibilityBus()
    {
        string? address = Environment.GetEnvironmentVariable(AccessibilityBusAddressVariable);
        if (string.IsNullOrEmpty(address))
        {
            address = ReadAccessibilityBusAddressFromSessionBus();
        }

        return string.IsNullOrEmpty(address) ? IntPtr.Zero : OpenConnection(address, DirectConnectionKind.Bus);
    }

    private static string? ReadAccessibilityBusAddressFromSessionBus()
    {
        string? sessionAddress = Environment.GetEnvironmentVariable(SessionBusAddressVariable);
        IntPtr session = string.IsNullOrEmpty(sessionAddress) ? IntPtr.Zero : OpenConnection(sessionAddress, DirectConnectionKind.Bus);
        if (session == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return ReadStringReply(session, AccessibilityBusName, AccessibilityBusPath, AccessibilityBusName, "GetAddress");
        }
        finally
        {
            CloseConnection(session);
        }
    }

    // The single string of a method's reply, or null when the call failed.
    private static string? ReadStringReply(IntPtr connection, string busName, string path, string interfaceName, string method)
    {
        IntPtr error = IntPtr.Zero;
        IntPtr reply = g_dbus_connection_call_sync(
            connection,
            busName,
            path,
            interfaceName,
            method,
            IntPtr.Zero,
            IntPtr.Zero,
            0,
            ActionTimeoutMilliseconds,
            IntPtr.Zero,
            ref error);
        if (error != IntPtr.Zero)
        {
            g_error_free(error);
            return null;
        }

        IntPtr child = g_variant_get_child_value(reply, UIntPtr.Zero);
        string? value = Marshal.PtrToStringUTF8(g_variant_get_string(child, IntPtr.Zero));
        g_variant_unref(child);
        g_variant_unref(reply);

        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static IntPtr OpenConnection(string address, DirectConnectionKind kind)
    {
        IntPtr error = IntPtr.Zero;
        IntPtr connection = g_dbus_connection_new_for_address_sync(address, (int)kind, IntPtr.Zero, IntPtr.Zero, ref error);
        if (error != IntPtr.Zero)
        {
            g_error_free(error);
            return IntPtr.Zero;
        }

        return connection;
    }

    private static void CloseConnection(IntPtr connection)
    {
        IntPtr error = IntPtr.Zero;
        g_dbus_connection_close_sync(connection, IntPtr.Zero, ref error);
        if (error != IntPtr.Zero)
        {
            g_error_free(error);
        }
        g_object_unref(connection);
    }

    [DllImport(Gio)]
    private static extern IntPtr g_dbus_connection_new_for_address_sync(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string address,
        int flags,
        IntPtr observer,
        IntPtr cancellable,
        ref IntPtr error);
    [DllImport(Gio)]
    private static extern IntPtr g_dbus_connection_call_sync(
        IntPtr connection,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? busName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string objectPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string interfaceName,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string methodName,
        IntPtr parameters,
        IntPtr replyType,
        int flags,
        int timeoutMilliseconds,
        IntPtr cancellable,
        ref IntPtr error);
    [DllImport(Gio)] private static extern int g_dbus_connection_close_sync(IntPtr connection, IntPtr cancellable, ref IntPtr error);
    [DllImport(Gio)] private static extern int g_dbus_is_name([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(GLibLibrary)] private static extern int g_variant_is_object_path([MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(GLibLibrary)] private static extern IntPtr g_variant_new_int32(int value);
    [DllImport(GLibLibrary)] private static extern IntPtr g_variant_new_tuple(IntPtr[] children, UIntPtr count);
    [DllImport(GLibLibrary)] private static extern IntPtr g_variant_get_child_value(IntPtr value, UIntPtr index);
    [DllImport(GLibLibrary)] private static extern IntPtr g_variant_get_string(IntPtr value, IntPtr length);
    [DllImport(GLibLibrary)] private static extern int g_variant_get_boolean(IntPtr value);
    [DllImport(GLibLibrary)] private static extern void g_variant_unref(IntPtr value);
}
#endif
