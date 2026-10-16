#if WINDOWS
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace KLaniBot.Platforms.Windows;

// The Windows node attaches to the already-running Outlook instance in its own user session. Its window must belong
// to the selected process before any mail or object-library data is read; this never starts a second Office instance.
internal sealed class WindowsOfficeObjectSession : IDisposable
{
    // IOleWindow supplies the native window handle; Outlook Explorer has no HWND automation property.
    [ComImport, Guid("00000114-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface OfficeWindow
    {
        void GetWindow(out IntPtr handle);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);
    }
    private readonly List<object> _objects = [];
    public object Application { get; }
    public object Namespace { get; }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    private static extern int CLSIDFromProgID(string programId, out Guid classId);
    [DllImport("oleaut32.dll", PreserveSig = true)]
    private static extern int GetActiveObject(ref Guid classId, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object value);

    // Throws InvalidOperationException when no Outlook instance matches the process, and rethrows Exception
    // after releasing acquired references on an Office refusal.
    public WindowsOfficeObjectSession(int processId)
    {
        int found = CLSIDFromProgID("Outlook.Application", out Guid classId);
        if (found < 0 || GetActiveObject(ref classId, IntPtr.Zero, out object application) < 0)
        {
            throw new InvalidOperationException("No running Outlook object model is available in this Windows session.");
        }
        Application = Keep(application);
        try
        {
            RequireProcess(processId);
            Namespace = Get(Application, "Session");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Throws InvalidOperationException when Outlook's active window belongs to another process.
    private void RequireProcess(int processId)
    {
        object? window = Call(Application, "ActiveExplorer");
        window ??= Call(Application, "ActiveInspector");
        if (window == null)
        {
            throw new InvalidOperationException("Outlook has no active object-model window to bind.");
        }
        var nativeWindow = (OfficeWindow)window;
        nativeWindow.GetWindow(out IntPtr handle);
        NativeMethods.GetWindowThreadProcessId(handle, out uint owner);
        if (owner != processId)
        {
            Dispose();
            throw new InvalidOperationException("The Outlook object model belongs to another process.");
        }
    }

    public object Get(object target, string member, params object[] arguments)
    {
        return Invoke(target, member, BindingFlags.GetProperty, arguments)!;
    }

    public object? Call(object target, string member, params object[] arguments)
    {
        return Invoke(target, member, BindingFlags.InvokeMethod, arguments);
    }

    public void Set(object target, string member, object value)
    {
        Invoke(target, member, BindingFlags.SetProperty, [value]);
    }

    private object? Invoke(object target, string member, BindingFlags operation, object[] arguments)
    {
        object? result = target.GetType().InvokeMember(member, operation | BindingFlags.OptionalParamBinding,
            null, target, arguments, CultureInfo.InvariantCulture);
        return result == null ? null : Keep(result);
    }

    private object Keep(object value)
    {
        if (Marshal.IsComObject(value) && !_objects.Any(existing => ReferenceEquals(existing, value)))
        {
            _objects.Add(value);
        }
        return value;
    }

    public void Dispose()
    {
        foreach (object value in _objects.AsEnumerable().Reverse())
        {
            try
            {
                Marshal.ReleaseComObject(value);
            }
            catch (InvalidComObjectException)
            {
                // Office can close an object while it is being inspected; releasing an already-disconnected reference is harmless.
            }
        }
        _objects.Clear();
    }
}
#endif
