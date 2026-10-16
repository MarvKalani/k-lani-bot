using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Platforms;

namespace KLaniBot.Actions;

// Use case: after an action with a postcondition the dispatcher waits for the result the agent expects. An application that
// answers with a dialog instead, such as an expired certificate, would leave the agent waiting out the whole timeout, and so
// would a person who stopped the session; between short slices the wait looks for both and ends at once.
internal sealed class PostconditionWait(IUiService ui, Func<bool> stillApproved)
{
    public const int DefaultMilliseconds = PostconditionDefaults.WaitMilliseconds;

    private const int SliceMilliseconds = 250;

    public JsonObject Wait(JsonObject condition, DialogWatch? watch)
    {
        int timeoutMilliseconds = condition["timeout_ms"]?.GetValue<int?>() ?? DefaultMilliseconds;

        if (watch == null)
        {
            return Result(Check(condition, timeoutMilliseconds));
        }
        return WaitInSlices(condition, timeoutMilliseconds, watch);
    }

    private JsonObject WaitInSlices(JsonObject condition, int timeoutMilliseconds, DialogWatch watch)
    {
        var waited = Stopwatch.StartNew();
        while (true)
        {
            long sliceStart = waited.ElapsedMilliseconds;
            int remaining = (int)Math.Max(0, timeoutMilliseconds - sliceStart);
            ElementCheckOutcome outcome = Check(condition, Math.Min(remaining, SliceMilliseconds));
            if (outcome.Success)
            {
                return Result(outcome);
            }
            string? dialogTitle = ui.ShowingModalWindowTitle(watch.ProcessId);
            if (dialogTitle != null && dialogTitle != watch.ModalTitleBefore)
            {
                return UnexpectedDialog(dialogTitle);
            }
            if (!stillApproved())
            {
                return StoppedByPerson();
            }
            if (waited.ElapsedMilliseconds >= timeoutMilliseconds)
            {
                return Result(outcome);
            }
            RestOfSlice(waited.ElapsedMilliseconds - sliceStart);
        }
    }

    // A check that failed at once, for example on an unsupported state, would otherwise turn the wait into a busy loop.
    private static void RestOfSlice(long sliceUsedMilliseconds)
    {
        if (sliceUsedMilliseconds < SliceMilliseconds)
        {
            Thread.Sleep((int)(SliceMilliseconds - sliceUsedMilliseconds));
        }
    }

    private ElementCheckOutcome Check(JsonObject condition, int timeoutMilliseconds)
    {
        return ui.VerifyElement(
            condition["id"]?.GetValue<int?>(),
            condition["name"]?.ToString(),
            condition["auto_id"]?.ToString(),
            condition["role"]?.ToString(),
            condition["pid"]?.GetValue<int?>(),
            condition["window_title"]?.ToString() ?? condition["title"]?.ToString(),
            condition["expected_state"]?.ToString() ?? "exists",
            condition["expected_text"]?.ToString(),
            timeoutMilliseconds);
    }

    private static JsonObject Result(ElementCheckOutcome outcome)
    {
        return new JsonObject
        {
            ["success"] = outcome.Success,
            ["message"] = outcome.Message,
            ["details"] = JsonSerializer.SerializeToNode(outcome.Details)
        };
    }

    private static JsonObject UnexpectedDialog(string windowTitle)
    {
        return new JsonObject
        {
            ["success"] = false,
            ["message"] = "A dialog opened instead of the expected result. Look at it before acting again; never repeat the action.",
            ["unexpected_dialog"] = new JsonObject { ["window_title"] = windowTitle }
        };
    }

    private static JsonObject StoppedByPerson()
    {
        return new JsonObject
        {
            ["success"] = false,
            ["message"] = "The person at the machine stopped the session while the node waited for the expected result.",
            ["stopped_by_person"] = true
        };
    }
}

// The process whose dialogs a wait watches, and the title of a modal window it already showed before the action.
internal sealed record DialogWatch(int ProcessId, string? ModalTitleBefore);
