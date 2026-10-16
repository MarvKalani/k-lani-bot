#if WINDOWS
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using KLaniBot.Localization;

namespace KLaniBot.Visibility;

// One FlowDocument makes summaries selectable across step boundaries. Expanders contain bounded read-only observations,
// and retained operation identities preserve expanded details as the node finishes a step or adds another one.
internal sealed class WindowsStepDocument(FrameworkElement theme, StepLogLevel level, HashSet<string> expanded, Action pauseFollowing)
{
    public Section? Current { get; private set; }

    public FlowDocument Create(StepList steps)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), FontSize = 14, FontFamily = new FontFamily("Segoe UI") };
        if (level == StepLogLevel.None)
        {
            document.Blocks.Add(Paragraph(UiTexts.Get(UiText.StepLogDisabled), 14));
            return document;
        }
        foreach (StepView step in steps.Before)
        {
            document.Blocks.Add(CreateSection(step, false, false, false));
        }
        if (steps.Now != null)
        {
            Current = CreateSection(steps.Now, true, false, false);
            document.Blocks.Add(Current);
        }
        foreach (StepView step in steps.Next)
        {
            document.Blocks.Add(CreateSection(step, false, true, steps.NextFromAgent));
        }

        return document;
    }

    private Section CreateSection(StepView step, bool current, bool planned, bool fromAgent)
    {
        var section = new Section
        {
            Padding = new Thickness(14, 12, 14, 8), Margin = new Thickness(0, 0, 5, 10),
            Background = WindowsWpfTheme.Brush(theme, "CardBrush"),
            BorderBrush = WindowsWpfTheme.Brush(theme, current ? "AccentBrush" : "LineBrush"),
            BorderThickness = new Thickness(current ? 2 : 1)
        };
        UiText state = planned ? (fromAgent ? UiText.StepNextFromAgent : UiText.StepNext) : StepLogText.StatusText(step.Status);
        string mark = !planned && step.Status == StepStatus.Completed ? "✓ " : current && !planned ? "● " : "○ ";
        Paragraph status = Paragraph(mark + UiTexts.Get(state).Replace("{0}", "").TrimEnd(), 12);
        status.Foreground = StatusBrush(step.Status, planned);
        status.FontWeight = FontWeights.SemiBold;
        section.Blocks.Add(status);
        section.Blocks.Add(Paragraph(step.Title.Length > 0 ? step.Title : step.Detail, 14));
        string summary = StepLogText.Summary(step);
        if (summary.Length > 0)
        {
            section.Blocks.Add(Paragraph(summary, 13));
        }
        AddDetails(section, step);

        return section;
    }

    private void AddDetails(Section section, StepView step)
    {
        if (step.Diagnostics == null)
        {
            return;
        }
        if (level is StepLogLevel.Debug or StepLogLevel.Verbose)
        {
            section.Blocks.Add(Details(step.OperationId + "/debug", UiText.StepDebugDetails, step.Diagnostics.Debug));
        }
        if (level == StepLogLevel.Verbose && step.Diagnostics.Verbose.Length > 0)
        {
            section.Blocks.Add(Details(step.OperationId + "/verbose", UiText.StepReturnedContent, step.Diagnostics.Verbose));
        }
    }

    private BlockUIContainer Details(string key, UiText label, string content)
    {
        var reader = new TextBox
        {
            Text = content, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        reader.SelectionChanged += (_, _) =>
        {
            if (reader.SelectionLength > 0)
            {
                pauseFollowing();
            }
        };
        var expander = new Expander
        {
            Header = UiTexts.Get(label), Content = reader, IsExpanded = expanded.Contains(key),
            Foreground = WindowsWpfTheme.Brush(theme, "TextBrush"), Margin = new Thickness(0, 5, 0, 5)
        };
        expander.Expanded += (_, _) => expanded.Add(key);
        expander.Collapsed += (_, _) => expanded.Remove(key);

        return new BlockUIContainer(expander);
    }

    private static Paragraph Paragraph(string content, int size)
    {
        return new Paragraph(new Run(content)) { FontSize = size, Margin = new Thickness(0, 0, 0, 8) };
    }

    private Brush StatusBrush(StepStatus status, bool planned)
    {
        string key = planned ? "MutedBrush" : status switch
        {
            StepStatus.Completed => "AccentBrush",
            StepStatus.Failed => "ErrorBrush",
            StepStatus.Paused or StepStatus.Uncertain => "WarningBrush",
            StepStatus.Running => "TextBrush",
            _ => throw new UnreachableException()
        };

        return WindowsWpfTheme.Brush(theme, key);
    }
}
#endif
