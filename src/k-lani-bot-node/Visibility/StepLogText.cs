using System.Diagnostics;
using System.Text;
using KLaniBot.Localization;

namespace KLaniBot.Visibility;

// The WPF copy command exports the displayed snapshot and level as plain text, including details across multiple steps.
public static class StepLogText
{
    public static string Format(StepList steps, StepLogLevel level)
    {
        if (level == StepLogLevel.None)
        {
            return "";
        }
        var text = new StringBuilder();
        text.AppendLine(StepEfficiencyText.Format(StepEfficiency.Measure(steps)));
        text.AppendLine();
        foreach (StepView step in steps.Before.Concat(steps.Now == null ? [] : new[] { steps.Now }).Concat(steps.Next))
        {
            bool planned = steps.Next.Contains(step);
            text.AppendLine(UiTexts.Get(planned ? UiText.StepNext : StatusText(step.Status)).Replace("{0}", "").TrimEnd());
            text.AppendLine(step.Title.Length > 0 ? step.Title : step.Detail);
            text.AppendLine(Summary(step));
            AppendDiagnostics(text, step, level);
            text.AppendLine();
        }

        return text.ToString().TrimEnd();
    }

    public static string Summary(StepView step)
    {
        return step.Diagnostics?.Summary is { Length: > 0 } summary ? summary : step.Result;
    }

    private static void AppendDiagnostics(StringBuilder text, StepView step, StepLogLevel level)
    {
        if (step.Diagnostics == null)
        {
            return;
        }
        if (level is StepLogLevel.Debug or StepLogLevel.Verbose)
        {
            text.AppendLine(UiTexts.Get(UiText.StepDebugDetails));
            text.AppendLine(step.Diagnostics.Debug);
        }
        if (level == StepLogLevel.Verbose && step.Diagnostics.Verbose.Length > 0)
        {
            text.AppendLine(UiTexts.Get(UiText.StepReturnedContent));
            text.AppendLine(step.Diagnostics.Verbose);
        }
    }

    public static UiText StatusText(StepStatus status)
    {
        return status switch
        {
            StepStatus.Running => UiText.StepNow,
            StepStatus.Completed => UiText.StepCompleted,
            StepStatus.Failed => UiText.StepFailed,
            StepStatus.Paused => UiText.StepPaused,
            StepStatus.Uncertain => UiText.StepUncertain,
            _ => throw new UnreachableException()
        };
    }

    public static UiText LevelText(StepLogLevel level)
    {
        return level switch
        {
            StepLogLevel.None => UiText.StepLevelNone,
            StepLogLevel.Standard => UiText.StepLevelStandard,
            StepLogLevel.Debug => UiText.StepLevelDebug,
            StepLogLevel.Verbose => UiText.StepLevelVerbose,
            _ => throw new UnreachableException()
        };
    }
}
