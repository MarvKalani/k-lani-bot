#if WINDOWS
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using KLaniBot.Startup;

namespace KLaniBot.Visibility;

// The WPF node uses Shell_NotifyIcon directly. A private HWND receives tray messages; no WinForms host or hidden
// controller indicator is created. Explorer restart recreates the same visible icon.
internal sealed class WindowsNativeTray : IDisposable
{
    private const uint AddIcon = 0;
    private const uint ModifyIcon = 1;
    private const uint DeleteIcon = 2;
    private const uint MessageIconAndTooltip = 7;
    private const int CallbackMessage = 0x8000 + 21;
    private const int RightButtonUp = 0x205;
    private const int LeftDoubleClick = 0x203;
    private readonly HwndSource _source;
    private readonly Dictionary<VisibilityStatus, Icon> _icons = new();
    private readonly uint _taskbarCreated;
    private TrayData _data;
    public event Action? OpenMenu;
    public event Action? OpenPending;

    // NOTIFYICONDATAW includes the Windows 7 GUID and balloon icon fields even when they are unused.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrayData
    {
        public uint Size;
        public IntPtr Window;
        public uint Id;
        public uint Flags;
        public uint Callback;
        public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tooltip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Information;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InformationTitle;
        public uint InformationFlags;
        public Guid Guid;
        public IntPtr BalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint operation, ref TrayData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);

    public static void ActivateMenu(IntPtr window)
    {
        SetForegroundWindow(window);
    }

    // Throws InvalidOperationException when Windows refuses the permanent tray indicator.
    public WindowsNativeTray()
    {
        _source = new HwndSource(new HwndSourceParameters(ProductIdentity.NodeName + " tray") { WindowStyle = 0 });
        _source.AddHook(Observe);
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        foreach (VisibilityStatus status in Enum.GetValues<VisibilityStatus>())
        {
            _icons[status] = CreateIcon(status);
        }
        _data = new TrayData
        {
            Size = (uint)Marshal.SizeOf<TrayData>(), Window = _source.Handle, Id = 1,
            Flags = MessageIconAndTooltip, Callback = CallbackMessage, Icon = _icons[VisibilityStatus.Idle].Handle,
            Tooltip = ProductIdentity.ProductName, Information = "", InformationTitle = "",
            State = 0, StateMask = 0, Timeout = 0, InformationFlags = 0, Guid = Guid.Empty, BalloonIcon = IntPtr.Zero
        };
        if (!Shell_NotifyIcon(AddIcon, ref _data))
        {
            throw new InvalidOperationException("Permanent node tray indicator unavailable.");
        }
    }

    // Throws InvalidOperationException when the visible icon can no longer be updated.
    public void Update(VisibilityStatus status, string tooltip)
    {
        _data.Icon = _icons[status].Handle;
        _data.Tooltip = tooltip.Length > 127 ? tooltip[..127] : tooltip;
        if (!Shell_NotifyIcon(ModifyIcon, ref _data))
        {
            throw new InvalidOperationException("Permanent node tray indicator could not be updated.");
        }
    }

    private IntPtr Observe(IntPtr window, int message, IntPtr first, IntPtr second, ref bool handled)
    {
        if (message == _taskbarCreated)
        {
            Shell_NotifyIcon(AddIcon, ref _data);
        }
        if (message == CallbackMessage)
        {
            if (second.ToInt32() == RightButtonUp)
            {
                OpenMenu?.Invoke();
            }
            else if (second.ToInt32() == LeftDoubleClick)
            {
                OpenPending?.Invoke();
            }
            handled = true;
        }

        return IntPtr.Zero;
    }

    private static Icon CreateIcon(VisibilityStatus status)
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        using var stream = new MemoryStream(ProductBranding.Read(ProductImage.Icon32));
        using var logo = Image.FromStream(stream);
        graphics.DrawImage(logo, new Rectangle(0, 0, 32, 32));
        Color color = status switch
        {
            VisibilityStatus.Idle => Color.Gray,
            VisibilityStatus.Connected => Color.DodgerBlue,
            VisibilityStatus.Active => Color.Red,
            _ => throw new System.Diagnostics.UnreachableException()
        };
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, 22, 22, 10, 10);
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    public void Dispose()
    {
        Shell_NotifyIcon(DeleteIcon, ref _data);
        _source.Dispose();
        foreach (Icon icon in _icons.Values)
        {
            icon.Dispose();
        }
    }
}
#endif
