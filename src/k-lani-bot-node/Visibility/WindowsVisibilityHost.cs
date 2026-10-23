#if WINDOWS
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using KLaniBot.Guard;
using KLaniBot.Platforms.Windows;
using KLaniBot.Startup;
using KLaniBot.Localization;
using Rectangle = System.Drawing.Rectangle;
using Brush = System.Windows.Media.Brush;

namespace KLaniBot.Visibility;

// The operated Windows node renders its tray menu, consent, task and selectable progress with WPF. Display updates never
// activate windows; native physical monitor coordinates keep the activity border correct at every DPI.
public sealed class WindowsVisibilityHost : NativeVisibilityHost
{
    private const int BorderThickness = 4;
    private const int AssignmentLineHeight = 23;
    private const int MaximumAssignmentLines = 5;
    private readonly WindowsNativeTray _tray;
    private readonly List<WindowsPassiveWindow> _borders = new();
    private readonly int _windowsSessionId;
    private WindowsPassiveWindow? _assignment;
    private TextBlock? _assignmentText;
    private TextBlock? _assignmentHeading;
    private WindowsStepListWindow? _stepList;
    private Window? _question;
    private ContextMenu? _menu;
    private string? _questionId;
    private string _geometry = "";
    private string _menuState = "";
    private int _bannerHeight;
    private DisplayDock _displayDock = DisplayDock.Right;
    private bool _displayCollapsed;
    private bool _displayHidden;

    // The person chooses where the node's own task display sits, independently of the application cartridge.
    private enum DisplayDock
    {
        Top,
        Left,
        Right
    }

    // Throws InvalidOperationException outside a signed-in interactive desktop.
    public WindowsVisibilityHost()
    {
        WindowsWpfRuntime.Initialize();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        _windowsSessionId = process.SessionId;
        if (!Environment.UserInteractive || _windowsSessionId == 0)
        {
            throw new InvalidOperationException("A visible node requires a logged-in interactive desktop.");
        }
        _tray = new WindowsNativeTray();
        _tray.OpenPending += ShowPending;
        _tray.OpenMenu += OpenMenu;
    }

    protected override void Apply(VisibilityState state)
    {
        _tray.Update(state.Status, ProductIdentity.ProductName + " " + ProductIdentity.DisplayVersion + " · " + StatusText(state));
        UpdateMenu(state);
        Rectangle[] monitors = state.Border ? WindowsScreenGeometry.ReadPhysicalMonitors() : [];
        string geometry = string.Join(";", monitors.Select(monitor => monitor.ToString())) + "|" + AssignmentLines(state, false).Count;
        if (_borders.Count > 0 && geometry != _geometry)
        {
            CloseActivityWindows();
        }
        _geometry = geometry;
        if (state.Border && _borders.Count == 0)
        {
            ShowActivityWindows(monitors, state);
        }
        if (!state.Border)
        {
            CloseActivityWindows();
        }
        foreach (WindowsPassiveWindow border in _borders)
        {
            border.Background = ActivityBrush(state);
            border.RaiseWithoutActivation();
        }
        UpdateAssignment(state);
        UpdateStepList(monitors, state);
    }

    private void UpdateAssignment(VisibilityState state)
    {
        if (_assignment != null && _assignmentText != null)
        {
            _assignmentText.Text = string.Join("\n", AssignmentLines(state, false));
            if (_assignmentHeading != null)
            {
                _assignmentHeading.Text = ProductIdentity.ProductName + " · " + StatusText(state);
            }
            if (_displayHidden)
            {
                _assignment.Hide();
            }
            else
            {
                _assignment.RaiseWithoutActivation();
            }
        }
    }

    private void UpdateMenu(VisibilityState state)
    {
        if (_menu?.IsOpen == true)
        {
            return;
        }
        string stamp = state.Status.ToString() + state.Unattended + state.ScreenBorder + state.AccessLocked + state.Prompt?.Challenge
            + state.Presentation + state.Pace + state.ModalDialogs + state.WindowInteraction + state.RecordActionScreenshots
            + _displayDock + _displayCollapsed + _displayHidden;
        if (stamp == _menuState)
        {
            return;
        }
        _menuState = stamp;
        _menu = BuildMenu(state);
    }

    private void OpenMenu()
    {
        if (_menu != null)
        {
            _menu.Placement = PlacementMode.MousePoint;
            _menu.IsOpen = true;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Input, new Action(FocusOpenedMenu));
        }
    }

    private void FocusOpenedMenu()
    {
        if (_menu?.IsOpen == true)
        {
            if (System.Windows.PresentationSource.FromVisual(_menu) is System.Windows.Interop.HwndSource popup)
            {
                WindowsNativeTray.ActivateMenu(popup.Handle);
            }
            _menu.Focus();
            System.Windows.Input.Keyboard.Focus(_menu);
        }
    }

    private void UpdateStepList(Rectangle[] monitors, VisibilityState state)
    {
        if (!state.Border || _assignment == null)
        {
            _stepList?.Dispose();
            _stepList = null;
            return;
        }
        if (_displayHidden || _displayCollapsed)
        {
            _stepList?.Hide();
            return;
        }
        _stepList ??= new WindowsStepListWindow();
        _stepList.ShowSteps(monitors[0], _bannerHeight, state.Steps ?? StepList.Empty, _displayDock == DisplayDock.Left);
    }

    private ContextMenu BuildMenu(VisibilityState state)
    {
        var menu = new ContextMenu
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 33, 50)),
            Foreground = Brushes.White, FontFamily = new FontFamily("Segoe UI"), FontSize = 14, Padding = new Thickness(8)
        };
        WindowsWpfTheme.ApplyResources(menu);
        var information = new MenuItem { Header = UiTexts.Get(UiText.MenuInformationGroup) };
        AddLabel(information, UiTexts.Format(UiText.MenuVersion, ProductIdentity.ProductName, ProductIdentity.DisplayVersion));
        AddLabel(information, UiTexts.Format(UiText.MenuNodeAndWindowsSession, ShownNodeId, _windowsSessionId));
        AddLabel(information, StatusText(state));
        AddLabel(information, UiTexts.Format(UiText.MenuMaster, ShownMasterOf(state)));
        if (state.Presentation != null)
        {
            AddLabel(information, UiTexts.Format(UiText.MenuClient, state.Presentation.ShownClient));
            AddLabel(information, UiTexts.Format(UiText.MenuPurpose, state.Presentation.ShownPurpose));
        }
        menu.Items.Add(information);
        menu.Items.Add(new Separator());
        if (state.Prompt != null)
        {
            AddAction(menu, UiText.AllowThisSession, () => Decide(SessionConsent.Allowed));
            AddAction(menu, UiText.NotNow, () => Decide(SessionConsent.Denied));
            AddAction(menu, UiText.AskAgainInFiveMinutes, () => Decide(SessionConsent.Deferred));
        }
        AddPace(menu, state);
        AddAction(menu, UiText.ShowPendingRequest, ShowPending);
        var settings = new MenuItem { Header = UiTexts.Get(UiText.MenuAccessGroup) };
        AddSettings(settings, state);
        menu.Items.Add(settings);
        menu.Items.Add(DiagnosticsMenu(state));
        menu.Items.Add(DisplayMenu());
        menu.Items.Add(new Separator());
        AddAction(menu, UiText.QuitProgram, Quit);

        return menu;
    }

    private MenuItem DisplayMenu()
    {
        var menu = new MenuItem { Header = UiTexts.Get(UiText.MenuDisplayGroup) };
        AddAction(menu, UiText.DisplayTop, () => SetDisplayDock(DisplayDock.Top), _displayDock == DisplayDock.Top);
        AddAction(menu, UiText.DisplayLeft, () => SetDisplayDock(DisplayDock.Left), _displayDock == DisplayDock.Left);
        AddAction(menu, UiText.DisplayRight, () => SetDisplayDock(DisplayDock.Right), _displayDock == DisplayDock.Right);
        AddAction(menu, UiText.DisplayCollapse, ToggleDisplayCollapse, _displayCollapsed);
        AddAction(menu, _displayHidden ? UiText.DisplayShow : UiText.DisplayHide, ToggleDisplayHidden);

        return menu;
    }

    private void SetDisplayDock(DisplayDock position)
    {
        _displayDock = position;
        _displayHidden = false;
        RebuildDisplay();
    }

    private void ToggleDisplayCollapse()
    {
        _displayCollapsed = !_displayCollapsed;
        RebuildDisplay();
    }

    private void ToggleDisplayHidden()
    {
        _displayHidden = !_displayHidden;
        RebuildDisplay();
    }

    private void RebuildDisplay()
    {
        CloseActivityWindows();
        _stepList?.Dispose();
        _stepList = null;
        _menuState = "";
        Controller?.Refresh();
    }

    private static void AddLabel(ItemsControl menu, string text)
    {
        menu.Items.Add(new MenuItem { Header = text, IsEnabled = false });
    }

    private static void AddAction(ItemsControl menu, UiText label, Action action, bool? selected = null)
    {
        var item = new MenuItem { Header = UiTexts.Get(label), IsCheckable = selected.HasValue, IsChecked = selected == true };
        if (MenuHints.TryGetValue(label, out UiText hint))
        {
            item.ToolTip = UiTexts.Get(hint);
        }
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static readonly Dictionary<UiText, UiText> MenuHints = new()
    {
        [UiText.MenuPause] = UiText.HintPace,
        [UiText.MenuSlowMotion] = UiText.HintPace,
        [UiText.MenuNormalSpeed] = UiText.HintPace,
        [UiText.ShowPendingRequest] = UiText.HintApproval,
        [UiText.AllowAutomatically] = UiText.HintAutomaticAccess,
        [UiText.OperateInBackground] = UiText.HintBackground,
        [UiText.AllowModalDialogs] = UiText.HintDialogs,
        [UiText.RecordScreenshots] = UiText.HintScreenshots,
        [UiText.OpenTrace] = UiText.HintTrace,
        [UiText.ShowScreenBorder] = UiText.HintBorder,
        [UiText.LockAllAccess] = UiText.HintAccessLock,
        [UiText.QuitProgram] = UiText.HintQuit
    };

    private MenuItem DiagnosticsMenu(VisibilityState state)
    {
        var menu = new MenuItem { Header = UiTexts.Get(UiText.MenuDiagnosticsGroup) };
        AddAction(menu, UiText.RecordScreenshots, SetScreenshotRecording, state.RecordActionScreenshots);
        if (HasTraceViewer)
        {
            AddAction(menu, UiText.OpenTrace, OpenTraceViewer);
        }
        else
        {
            var unavailable = new MenuItem
            {
                Header = UiTexts.Get(UiText.MenuTraceUnavailable), IsEnabled = false, ToolTip = UiTexts.Get(UiText.HintTrace)
            };
            ToolTipService.SetShowOnDisabled(unavailable, true);
            menu.Items.Add(unavailable);
        }

        return menu;
    }

    private void AddPace(ContextMenu menu, VisibilityState state)
    {
        AddAction(menu, UiText.MenuPause, PauseAgent, state.Pace == Pace.Paused);
        AddAction(menu, UiText.MenuSlowMotion, SlowMotion, state.Pace == Pace.SlowMotion);
        AddAction(menu, UiText.MenuNormalSpeed, NormalSpeed, state.Pace == Pace.Normal);
    }

    private void AddSettings(ItemsControl menu, VisibilityState state)
    {
        AddAction(menu, UiText.AllowAutomatically, SetUnattended, state.Unattended);
        bool background = state.WindowInteraction == WindowInteractionMode.BackgroundSemantic;
        AddAction(menu, UiText.OperateInBackground, SetBackgroundInteraction, background);
        AddAction(menu, UiText.AllowModalDialogs, SetModalInteraction, state.ModalDialogs == ModalDialogMode.AllowInteraction);
        AddAction(menu, UiText.ShowScreenBorder, SetBorder, state.ScreenBorder);
        AddAction(menu, UiText.LockAllAccess, ToggleAccessLock, state.AccessLocked);
    }

    private void CloseActivityWindows()
    {
        foreach (WindowsPassiveWindow border in _borders)
        {
            border.Close();
        }
        _borders.Clear();
        _assignment?.Close();
        _assignment = null;
        _assignmentText = null;
        _assignmentHeading = null;
    }

    private static Brush ActivityBrush(VisibilityState state)
    {
        return state.Status == VisibilityStatus.Active ? Brushes.Red : Brushes.Teal;
    }

    private void ShowActivityWindows(Rectangle[] monitors, VisibilityState state)
    {
        foreach (Rectangle area in monitors)
        {
            Rectangle[] edges =
            [
                new(area.Left, area.Top, area.Width, BorderThickness),
                new(area.Left, area.Bottom - BorderThickness, area.Width, BorderThickness),
                new(area.Left, area.Top, BorderThickness, area.Height),
                new(area.Right - BorderThickness, area.Top, BorderThickness, area.Height)
            ];
            foreach (Rectangle edge in edges)
            {
                var border = new WindowsPassiveWindow(true) { Background = ActivityBrush(state) };
                border.ShowAt(edge);
                _borders.Add(border);
            }
        }
        ShowAssignment(monitors[0], state);
    }

    private void ShowAssignment(Rectangle primary, VisibilityState state)
    {
        // Docked at a side, the task display and the step list form one column of equal width.
        int width = Math.Min(_displayDock == DisplayDock.Top ? 900 : WindowsStepListWindow.PanelWidth, primary.Width - 32);
        _assignment = new WindowsPassiveWindow(false);
        WindowsWpfTheme.Apply(_assignment);
        _assignment.Background = Brushes.Transparent;
        var content = new StackPanel();
        TextBlock heading = WindowsWpfTheme.Text(ProductIdentity.ProductName + " · " + StatusText(state), 18);
        _assignmentHeading = heading;
        heading.Foreground = WindowsWpfTheme.Brush(_assignment, "AccentBrush");
        heading.FontWeight = FontWeights.SemiBold;
        heading.Cursor = System.Windows.Input.Cursors.SizeAll;
        heading.MouseLeftButtonDown += (_, _) => _assignment?.MoveByPerson();
        _assignmentText = WindowsWpfTheme.Text(string.Join("\n", AssignmentLines(state, false)));
        content.Children.Add(AssignmentHeading(heading));
        // Long client, master or task lines wrap. A fixed line count would cut them off, so the text scrolls inside
        // a bounded area and the person can still read what the remote control is for.
        int visibleLines = Math.Min(AssignmentLines(state, false).Count, MaximumAssignmentLines);
        if (!_displayCollapsed)
        {
            content.Children.Add(new ScrollViewer
            {
                Content = _assignmentText,
                Height = visibleLines * AssignmentLineHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            });
        }
        _assignment.Content = WindowsWpfTheme.Surface(_assignment, content, 16);
        _bannerHeight = _displayCollapsed ? 66 : 65 + visibleLines * AssignmentLineHeight;
        int left = _displayDock switch
        {
            DisplayDock.Top => primary.Left + (primary.Width - width) / 2,
            DisplayDock.Left => primary.Left + 16,
            DisplayDock.Right => primary.Right - width - 16,
            _ => throw new System.Diagnostics.UnreachableException()
        };
        if (!_displayHidden)
        {
            _assignment.ShowAt(new Rectangle(left, primary.Top + 16, width, _bannerHeight));
        }
    }

    private DockPanel AssignmentHeading(TextBlock heading)
    {
        var layout = new DockPanel();
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(DisplayButton(_displayCollapsed ? "▸" : "−", UiText.DisplayCollapse, ToggleDisplayCollapse));
        controls.Children.Add(DisplayButton("×", UiText.DisplayHide, ToggleDisplayHidden));
        DockPanel.SetDock(controls, Dock.Right);
        layout.Children.Add(controls);
        layout.Children.Add(heading);

        return layout;
    }

    private static Button DisplayButton(string symbol, UiText hint, Action action)
    {
        var button = new Button
        {
            Content = symbol, ToolTip = UiTexts.Get(hint), MinWidth = 32,
            Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(6, 2, 6, 2), Focusable = false
        };
        button.Click += (_, _) => action();

        return button;
    }

    protected override void ShowQuestion(ConsentPrompt? prompt)
    {
        if (prompt == null)
        {
            _question?.Close();
            _question = null;
            _questionId = null;
            return;
        }
        if (_questionId == prompt.Challenge && _question != null)
        {
            _question.Activate();
            return;
        }
        _question?.Close();
        _questionId = prompt.Challenge;
        _question = new Window
        {
            Title = UiTexts.Format(UiText.ConsentWindowsTitle, ProductIdentity.ProductName + " " + ProductIdentity.DisplayVersion),
            Width = 650, SizeToContent = SizeToContent.Height, MaxHeight = 800, Topmost = true,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        WindowsWpfTheme.Apply(_question);
        _question.Content = QuestionLayout(prompt.Presentation ?? SessionPresentation.Create(null, null));
        _question.Closed += (_, _) => _question = null;
        _question.Show();
    }

    private Border QuestionLayout(SessionPresentation details)
    {
        var layout = new StackPanel();
        layout.Children.Add(WindowsWpfTheme.Logo());
        layout.Children.Add(WindowsWpfTheme.Text(UiTexts.Get(UiText.ConsentHeading), 21));
        layout.Children.Add(WindowsWpfTheme.Text(UiTexts.Format(UiText.ConsentThisComputer, details.ShownNode)));
        layout.Children.Add(WindowsWpfTheme.Text(UiTexts.Format(UiText.ConsentPairedMaster, details.ShownMaster)));
        layout.Children.Add(WindowsWpfTheme.Text(UiTexts.Format(UiText.ConsentRequestingAi, details.ShownClient)));
        layout.Children.Add(WindowsWpfTheme.Text(UiTexts.Format(UiText.ConsentPurpose, details.ShownPurpose), 17));
        layout.Children.Add(WindowsWpfTheme.Text(UiTexts.Format(UiText.ConsentAllowedRights, details.DescribeRights())));
        layout.Children.Add(WindowsWpfTheme.Text(UiTexts.Get(UiText.ConsentSessionOnly), 13));
        layout.Children.Add(QuestionButtons());

        return WindowsWpfTheme.Surface(_question!, layout, 24);
    }

    private WrapPanel QuestionButtons()
    {
        var buttons = new WrapPanel();
        foreach (ConsentChoice consentChoice in ConsentChoices)
        {
            SessionConsent choice = consentChoice.Decision;
            var button = new Button { Content = UiTexts.Get(consentChoice.Label) };
            button.Click += (_, _) => Decide(choice);
            buttons.Children.Add(button);
        }

        return buttons;
    }

    protected override void Pump()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    protected override IEscapeKeyWatch? StartEscapeKeyWatch()
    {
        if (WindowsEscapeKeyWatch.TryStart(out WindowsEscapeKeyWatch? watch))
        {
            return watch;
        }
        Console.Error.WriteLine("ESC cannot end sessions: the keyboard hook was refused.");

        return null;
    }

    protected override void CloseNative()
    {
        _question?.Close();
        CloseActivityWindows();
        _stepList?.Dispose();
        _tray.Dispose();
    }
}
#endif
