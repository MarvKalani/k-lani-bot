#if WINDOWS
using Rectangle = System.Drawing.Rectangle;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace KLaniBot.Visibility;

// Node overlays never activate programmatically. An explicitly interactive log accepts the person's focus for selection
// and keyboard copying; passive activity borders and banners retain their non-activating behavior.
internal sealed class WindowsPassiveWindow : Window
{
    private const int ExtendedWindowStyle = -20;
    private const int NoActivateStyle = 0x08000000;
    private const int ToolWindowStyle = 0x80;
    private const int TransparentStyle = 0x20;
    private const int MouseActivateMessage = 0x21;
    private const int HitTestMessage = 0x84;
    private const int DoNotActivate = 3;
    private readonly bool _clickThrough;
    private readonly bool _allowPersonActivation;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr first, IntPtr second);

    public void MoveByPerson()
    {
        const int NonClientLeftButtonDown = 0xA1;
        const int CaptionHit = 2;
        ReleaseCapture();
        SendMessage(new WindowInteropHelper(this).Handle, NonClientLeftButtonDown, new IntPtr(CaptionHit), IntPtr.Zero);
    }

    public WindowsPassiveWindow(bool clickThrough, bool allowPersonActivation = false)
    {
        _clickThrough = clickThrough;
        _allowPersonActivation = allowPersonActivation;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = allowPersonActivation;
        SourceInitialized += ConfigureNativeWindow;
    }

    private void ConfigureNativeWindow(object? sender, EventArgs arguments)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        int styles = GetWindowLong(handle, ExtendedWindowStyle) | ToolWindowStyle;
        if (!_allowPersonActivation)
        {
            styles |= NoActivateStyle;
        }
        if (_clickThrough)
        {
            styles |= TransparentStyle;
        }
        SetWindowLong(handle, ExtendedWindowStyle, styles);
        HwndSource.FromHwnd(handle)?.AddHook(ObserveNativeMessage);
    }

    private IntPtr ObserveNativeMessage(IntPtr window, int message, IntPtr first, IntPtr second, ref bool handled)
    {
        if (message == MouseActivateMessage && !_allowPersonActivation)
        {
            handled = true;
            return new IntPtr(DoNotActivate);
        }
        if (_clickThrough && message == HitTestMessage)
        {
            handled = true;
            return new IntPtr(-1);
        }

        return IntPtr.Zero;
    }

    // Throws InvalidOperationException when the physical overlay position cannot be applied.
    public void ShowAt(Rectangle rectangle)
    {
        if (!IsVisible)
        {
            Show();
        }
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (!SetWindowPos(handle, new IntPtr(-1), rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, 0x10 | 0x40))
        {
            throw new InvalidOperationException("The node overlay could not be placed on the physical desktop.");
        }
    }

    // Throws InvalidOperationException when the overlay cannot remain above the operated window.
    public void RaiseWithoutActivation()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (!SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x40))
        {
            throw new InvalidOperationException("The node overlay could not remain visible.");
        }
    }
}
#endif
