#if WINDOWS
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using KLaniBot.Visibility;

namespace KLaniBot.ControllerTray;

// The Windows controller tray: the same Shell_NotifyIcon wrapper the node uses for its icon, and a WPF context menu in the
// product's shared style on right click. It never creates the node's border, banner or consent window.
internal sealed class WindowsControllerTray : ControllerTrayHost
{
    private readonly Dispatcher _dispatcher;
    private readonly WindowsNativeTray _tray;
    private ContextMenu? _menu;
    private ControllerTrayView? _pending;

    // Throws InvalidOperationException when Windows refuses the tray icon.
    public WindowsControllerTray()
    {
        WindowsWpfRuntime.Initialize();
        _dispatcher = Dispatcher.CurrentDispatcher;
        _tray = new WindowsNativeTray();
        _tray.OpenMenu += OpenMenu;
        _tray.OpenPending += OpenMenu;
    }

    // The node's tray knows three colours; off and a missing master both show grey and differ in the hover text.
    private static VisibilityStatus StatusOf(ControllerTrayIcon icon)
    {
        return icon switch
        {
            ControllerTrayIcon.On => VisibilityStatus.Connected,
            ControllerTrayIcon.Working => VisibilityStatus.Active,
            ControllerTrayIcon.Off => VisibilityStatus.Idle,
            ControllerTrayIcon.MasterMissing => VisibilityStatus.Idle,
            _ => throw new UnreachableException()
        };
    }

    public override void Show(ControllerTrayView view)
    {
        _dispatcher.BeginInvoke(() => Draw(view));
    }

    // An open menu is left alone and replaced when it closes: node vitals change every poll, and closing the menu each
    // time would take it away while the person reads it.
    private void Draw(ControllerTrayView view)
    {
        _tray.Update(StatusOf(view.Icon), view.Tooltip);
        if (_menu?.IsOpen == true)
        {
            _pending = view;
            return;
        }
        _menu = BuildMenu(view);
    }

    private void DrawPending(object sender, RoutedEventArgs arguments)
    {
        if (_pending is { } view)
        {
            _pending = null;
            _menu = BuildMenu(view);
        }
    }

    private ContextMenu BuildMenu(ControllerTrayView view)
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(Color.FromRgb(20, 33, 50)),
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Padding = new Thickness(8)
        };
        WindowsWpfTheme.ApplyResources(menu);
        menu.Closed += DrawPending;
        foreach (ControllerTrayEntry entry in view.Entries)
        {
            menu.Items.Add(CreateItem(entry));
        }
        return menu;
    }

    private Control CreateItem(ControllerTrayEntry entry)
    {
        if (entry.Kind == ControllerTrayEntryKind.Separator)
        {
            return new Separator();
        }
        var item = new MenuItem
        {
            Header = entry.Text,
            IsEnabled = entry.Command != null,
            FontWeight = entry.Kind == ControllerTrayEntryKind.Heading ? FontWeights.SemiBold : FontWeights.Normal,
            Margin = new Thickness(entry.Indented ? 18 : 0, 0, 0, 0)
        };
        if (entry.Command is { } command)
        {
            item.Click += (_, _) => Choose(command);
        }
        return item;
    }

    private void OpenMenu()
    {
        if (_menu == null)
        {
            return;
        }
        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
        if (PresentationSource.FromVisual(_menu) is System.Windows.Interop.HwndSource popup)
        {
            WindowsNativeTray.ActivateMenu(popup.Handle);
        }
    }

    public override void Run()
    {
        Dispatcher.Run();
    }

    public override void Close()
    {
        _dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
    }

    public override void Dispose()
    {
        _tray.Dispose();
    }
}
#endif
