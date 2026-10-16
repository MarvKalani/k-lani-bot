#if WINDOWS
using System.Runtime.InteropServices;
using Rectangle = System.Drawing.Rectangle;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KLaniBot.Localization;

namespace KLaniBot.Visibility;

// The person selects and copies a live WPF log across steps. Selecting or scrolling freezes only the displayed snapshot;
// explicit refresh and Follow live resume it, while the bot and its access controls continue independently.
internal sealed class WindowsStepListWindow : IDisposable
{
    private const int PreferredWidth = 620;
    private const int PreferredHeight = 560;
    private const int ScreenGap = 16;
    private readonly WindowsPassiveWindow _window = new(false, true);
    private readonly RichTextBox _reader = new();
    private readonly CheckBox _follow = new() { IsChecked = true };
    private readonly ComboBox _levels = new();
    private readonly TextBlock _efficiency = new();
    private readonly HashSet<string> _expanded = new();
    private StepList _latest = StepList.Empty;
    private StepList _shown = StepList.Empty;
    private StepLogLevel _level = StepLogLevel.Standard;
    private Rectangle? _placedAt;
    private bool _rendering;

    public WindowsStepListWindow()
    {
        WindowsWpfTheme.Apply(_window);
        _window.Background = Brushes.Transparent;
        _window.Title = UiTexts.Get(UiText.StepListTitle);
        ConfigureReader();
        var layout = new DockPanel();
        StackPanel toolbar = CreateToolbar();
        DockPanel.SetDock(toolbar, Dock.Top);
        layout.Children.Add(toolbar);
        layout.Children.Add(_reader);
        _window.Content = WindowsWpfTheme.Surface(_window, layout);
    }

    private void ConfigureReader()
    {
        _reader.IsReadOnly = true;
        _reader.IsDocumentEnabled = true;
        _reader.Background = Brushes.Transparent;
        _reader.Foreground = WindowsWpfTheme.Brush(_window, "TextBrush");
        _reader.BorderThickness = new Thickness(0);
        _reader.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _reader.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _reader.SelectionChanged += (_, _) =>
        {
            if (!_rendering && !_reader.Selection.IsEmpty)
            {
                PauseFollowing();
            }
        };
        _reader.PreviewMouseWheel += (_, _) => PauseFollowing();
    }

    private StackPanel CreateToolbar()
    {
        var toolbar = new StackPanel();
        TextBlock heading = WindowsWpfTheme.Text(UiTexts.Get(UiText.StepListTitle), 20);
        heading.FontWeight = FontWeights.SemiBold;
        heading.Cursor = System.Windows.Input.Cursors.SizeAll;
        heading.MouseLeftButtonDown += (_, _) => _window.MoveByPerson();
        toolbar.Children.Add(heading);
        var controls = new WrapPanel();
        ConfigureLevels();
        controls.Children.Add(_levels);
        controls.Children.Add(CreateButton(UiText.StepCopyAll, CopyAll));
        controls.Children.Add(CreateButton(UiText.StepRefresh, RenderLatest));
        _follow.Content = UiTexts.Get(UiText.StepFollowLive);
        _follow.ToolTip = UiTexts.Get(UiText.StepLogSnapshot);
        _follow.Checked += (_, _) => RenderLatest();
        controls.Children.Add(_follow);
        toolbar.Children.Add(controls);
        _efficiency.TextWrapping = TextWrapping.Wrap;
        _efficiency.Foreground = WindowsWpfTheme.Brush(_window, "TextBrush");
        _efficiency.Margin = new Thickness(0, 8, 0, 8);
        toolbar.Children.Add(_efficiency);

        return toolbar;
    }

    private void ConfigureLevels()
    {
        _levels.MinWidth = 112;
        foreach (StepLogLevel level in Enum.GetValues<StepLogLevel>())
        {
            _levels.Items.Add(new ComboBoxItem { Content = UiTexts.Get(StepLogText.LevelText(level)), Tag = level });
        }
        _levels.SelectedIndex = (int)_level;
        _levels.SelectionChanged += (_, _) =>
        {
            _level = (StepLogLevel)((ComboBoxItem)_levels.SelectedItem).Tag;
            RenderLatest();
        };
    }

    private static Button CreateButton(UiText label, Action action)
    {
        var button = new Button { Content = UiTexts.Get(label), Padding = new Thickness(10, 6, 10, 6) };
        button.Click += (_, _) => action();

        return button;
    }

    public void ShowSteps(Rectangle screen, double bannerHeight, StepList steps, bool atLeft = false)
    {
        _latest = steps;
        if (_follow.IsChecked == true && !SameSteps(_shown, steps))
        {
            RenderLatest();
        }
        int width = Math.Min(PreferredWidth, screen.Width - ScreenGap * 2);
        int height = Math.Min(PreferredHeight, screen.Height - (int)bannerHeight - ScreenGap * 3);
        int left = atLeft ? screen.Left + ScreenGap : screen.Right - width - ScreenGap;
        var placement = new Rectangle(left, screen.Top + (int)bannerHeight + ScreenGap, width, height);
        if (_placedAt != placement)
        {
            _placedAt = placement;
            _window.ShowAt(placement);
        }
    }

    private static bool SameSteps(StepList first, StepList second)
    {
        return first.Now == second.Now && first.NextFromAgent == second.NextFromAgent
            && first.Before.SequenceEqual(second.Before) && first.Next.SequenceEqual(second.Next);
    }

    private void RenderLatest()
    {
        _rendering = true;
        try
        {
            _shown = _latest;
            _efficiency.Text = _level == StepLogLevel.None ? "" : StepEfficiencyText.Format(StepEfficiency.Measure(_shown));
            var document = new WindowsStepDocument(_window, _level, _expanded, PauseFollowing);
            _reader.Document = document.Create(_shown);
            if (_follow.IsChecked == true)
            {
                document.Current?.BringIntoView();
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private void PauseFollowing()
    {
        _follow.IsChecked = false;
    }

    private void CopyAll()
    {
        string text = StepLogText.Format(_shown, _level);
        if (text.Length == 0)
        {
            return;
        }
        try
        {
            Clipboard.SetText(text);
        }
        catch (ExternalException)
        {
            _reader.ToolTip = UiTexts.Get(UiText.StepCopyFailed);
        }
    }

    public void Hide()
    {
        _window.Hide();
        _placedAt = null;
    }

    public void Dispose()
    {
        _window.Close();
    }
}
#endif
