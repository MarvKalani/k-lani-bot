using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Actions;

// What an AI client may conclude from the result of one action. The order of the members is not
// meaningful; the wire names below are the contract and are pinned by a test.
public enum ActionOutcome
{
    // Passed every check and was written to the journal, but the result is not known yet. This is
    // an internal state of the journal. If the node stops here, the operation is treated as Unknown.
    Admitted,

    // Handed to the operating system. It says nothing about whether the application did what was
    // meant; only a postcondition can turn it into Verified.
    Dispatched,

    // A read, or a file operation inside the node, ran to its end. There is no later state to verify.
    Completed,

    // Dispatched, and the declared postcondition was observed afterwards.
    Verified,

    // Dispatched, but the declared postcondition did not hold. Something may have changed.
    PostconditionFailed,

    // Refused or failed before any effect on the machine. Safe to try again under the same id.
    NotDispatched,

    // The action may or may not have run (lost connection, crash after dispatch). Never repeat it
    // automatically; look at the machine first.
    Unknown,

    // A read-only action failed.
    Failed,

    // A macro stopped after some steps had run, so part of its effect is on the machine.
    Partial,

    // A macro step selected a different number of elements than the recipe expects, and the macro
    // stopped before it could act on an ambiguous target.
    SelectorCardinalityFailed,

    // A macro stopped because its collected results grew past the size limit.
    ResultBudgetExceeded
}

// The only place that translates between the enum and the words that clients, journals and
// traces contain. Everything else in the node compares enums.
public static class ActionOutcomeNames
{
    public static string ToWireName(this ActionOutcome outcome)
    {
        return outcome switch
        {
            ActionOutcome.Admitted => "admitted",
            ActionOutcome.Dispatched => "dispatched",
            ActionOutcome.Completed => "completed",
            ActionOutcome.Verified => "verified",
            ActionOutcome.PostconditionFailed => "postcondition_failed",
            ActionOutcome.NotDispatched => "not_dispatched",
            ActionOutcome.Unknown => "unknown",
            ActionOutcome.Failed => "failed",
            ActionOutcome.Partial => "partial",
            ActionOutcome.SelectorCardinalityFailed => "selector_cardinality_failed",
            ActionOutcome.ResultBudgetExceeded => "result_budget_exceeded",
            _ => throw new UnreachableException()
        };
    }

    // A word that is not in the table becomes Unknown instead of an exception: an actuator of a
    // newer version must not make the supervisor fail, and "Unknown" is the cautious reading. The
    // caller can be told about the unexpected word so that it is not lost.
    public static ActionOutcome Parse(string? wireName, Action<string>? reportUnrecognized = null)
    {
        if (wireName == null)
        {
            return ActionOutcome.Unknown;
        }
        foreach (ActionOutcome outcome in Enum.GetValues<ActionOutcome>())
        {
            if (outcome.ToWireName() == wireName)
            {
                return outcome;
            }
        }
        reportUnrecognized?.Invoke(wireName);
        return ActionOutcome.Unknown;
    }

    public static ActionOutcome Read(JsonObject result, Action<string>? reportUnrecognized = null)
    {
        return Parse(result["outcome"]?.ToString(), reportUnrecognized);
    }

    // Used when a journal entry is turned back into an answer: the action ran and nothing in the
    // outcome says it went wrong.
    public static bool ReportsSuccess(this ActionOutcome outcome)
    {
        return outcome is ActionOutcome.Completed or ActionOutcome.Verified or ActionOutcome.Dispatched;
    }
}
