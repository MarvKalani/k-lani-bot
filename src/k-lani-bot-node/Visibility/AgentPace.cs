using System.Text;

namespace KLaniBot.Visibility;

// Use case: the person at the machine presses ESC to look at what the agent is doing, then lets it go on at normal speed or in
// slow motion, where every step is announced in the assignment box and waits before it runs. Chosen in the tray menu.
public enum Pace
{
    Normal,

    // Every new step waits for the person; a step that met a pause is never dispatched afterwards.
    Paused,

    // Every step is shown and runs after the administrator's step time.
    SlowMotion
}

// What a step does, in words the person understands; the hosts show it in the person's language.
public enum StepKind
{
    Read,
    Click,
    Type,
    Input,
    Window,
    Launch,
    Clipboard,
    Files,
    Command,
    Macro,
    Other,

    // The agent's own words about a step it plans; shown marked as the agent's claim.
    Announced
}

// The answer of the pace gate to a waiting step.
public enum PaceDecision
{
    Proceed,

    // The step met a pause and was not dispatched; the agent observes again before it goes on.
    PausedByPerson
}

// The node keeps selectable summaries and bounded observation details locally, independently of the MCP result catalog.
public sealed record StepView(StepKind Kind, string Detail, StepStatus Status = StepStatus.Running, string Result = "", string Title = "",
    string OperationId = "", StepDiagnostics? Diagnostics = null);

// Local display levels change presentation only; they never request more application data or expand MCP tool results.
public enum StepLogLevel
{
    None,
    Standard,
    Debug,
    Verbose
}

// The completed action supplies these immutable measurements before its result leaves the desktop worker.
public sealed record StepDiagnostics(string Summary, string Debug, string Verbose, long ArgumentJsonBytes,
    long ResultJsonBytes, long ElapsedMilliseconds, DateTimeOffset CompletedAt);

// The result the node observed for a displayed step; an attempt is never shown as completed merely because another step began.
public enum StepStatus
{
    Running,
    Completed,
    Failed,
    Paused,
    Uncertain
}

// The steps the assignment box shows: the last few before, the one now and the ones planned next.
public sealed record StepList(IReadOnlyList<StepView> Before, StepView? Now, IReadOnlyList<StepView> Next, bool NextFromAgent)
{
    public static readonly StepList Empty = new([], null, [], false);
}

// Text that ends up in the always-visible box comes from programs and from the agent. Line breaks, control characters and
// bidirectional overrides could make it pose as a line of the node's own, so they are removed and the length is capped.
public static class StepText
{
    public const int MaximumCharacters = 200;

    public static string Clean(string text)
    {
        var cleaned = new StringBuilder();
        foreach (char character in text)
        {
            if (cleaned.Length >= MaximumCharacters)
            {
                break;
            }
            cleaned.Append(IsHidden(character) ? ' ' : character);
        }
        return cleaned.ToString().Trim();
    }

    private static bool IsHidden(char character)
    {
        bool bidirectionalOverride = character is >= '‪' and <= '‮' or >= '⁦' and <= '⁩';
        return char.IsControl(character) || bidirectionalOverride || character is '‎' or '‏';
    }
}
