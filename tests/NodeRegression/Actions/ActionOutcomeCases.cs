using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Audit;

// The words in results, journals and traces are a contract with clients that are not rebuilt with the
// node. These cases pin every word, so renaming an enum member can never change what is sent.
internal static class ActionOutcomeCases
{
    // An outcome and the word the wire protocol uses for it; the table pins every word.
    private sealed record OutcomeWord(ActionOutcome Outcome, string Word);

    // An error code and the word the wire protocol uses for it; the table pins every word.
    private sealed record ErrorCodeWord(ActionErrorCode Code, string Word);

    private static readonly OutcomeWord[] OutcomeWords =
    [
        new(ActionOutcome.Admitted, "admitted"),
        new(ActionOutcome.Dispatched, "dispatched"),
        new(ActionOutcome.Completed, "completed"),
        new(ActionOutcome.Verified, "verified"),
        new(ActionOutcome.PostconditionFailed, "postcondition_failed"),
        new(ActionOutcome.NotDispatched, "not_dispatched"),
        new(ActionOutcome.Unknown, "unknown"),
        new(ActionOutcome.Failed, "failed"),
        new(ActionOutcome.Partial, "partial"),
        new(ActionOutcome.SelectorCardinalityFailed, "selector_cardinality_failed"),
        new(ActionOutcome.ResultBudgetExceeded, "result_budget_exceeded")
    ];

    private static readonly ErrorCodeWord[] ErrorCodeWords =
    [
        new(ActionErrorCode.Denied, "denied"),
        new(ActionErrorCode.InvalidRequest, "invalid_request"),
        new(ActionErrorCode.Unsupported, "unsupported"),
        new(ActionErrorCode.Busy, "busy"),
        new(ActionErrorCode.WorkerStopped, "worker_stopped"),
        new(ActionErrorCode.WorkerUnavailable, "worker_unavailable"),
        new(ActionErrorCode.TransportError, "transport_error"),
        new(ActionErrorCode.Cancelled, "cancelled"),
        new(ActionErrorCode.Pending, "pending"),
        new(ActionErrorCode.UnknownOperation, "unknown_operation"),
        new(ActionErrorCode.IndicatorUnavailable, "indicator_unavailable"),
        new(ActionErrorCode.ConsentRequired, "consent_required"),
        new(ActionErrorCode.ConsentDenied, "consent_denied"),
        new(ActionErrorCode.ConsentDeferred, "consent_deferred"),
        new(ActionErrorCode.RequestIdExpired, "request_id_expired"),
        new(ActionErrorCode.RequestIdFromFuture, "request_id_from_future"),
        new(ActionErrorCode.OperationLedgerFull, "operation_ledger_full"),
        new(ActionErrorCode.BackupRequired, "backup_required"),
        new(ActionErrorCode.InvalidRequestId, "invalid_request_id"),
        new(ActionErrorCode.ImageTooLarge, "image_too_large"),
        new(ActionErrorCode.PausedByPerson, "paused_by_person"),
        new(ActionErrorCode.NoAccessibilityTree, "no_accessibility_tree")
    ];

    public static int Run(string temporaryRoot)
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Action outcome: " + name);
            }
            passed++;
        }
        EveryOutcomeHasItsWord(Check);
        EveryErrorCodeHasItsWord(Check);
        UnrecognizedOutcomeBecomesUnknown(Check);
        FailureResultsKeepTheirShape(Check);
        JournalRecoveryReadsEveryWrittenWord(Check, temporaryRoot);
        return passed;
    }

    private static void EveryOutcomeHasItsWord(Action<bool, string> check)
    {
        check(OutcomeWords.Length == Enum.GetValues<ActionOutcome>().Length, "the golden list names every outcome");
        foreach ((ActionOutcome outcome, string word) in OutcomeWords)
        {
            check(outcome.ToWireName() == word, "wire word of " + outcome);
            check(ActionOutcomeNames.Parse(word) == outcome, "parsing " + word);
        }
        check(OutcomeWords.Select(entry => entry.Word).Distinct().Count() == OutcomeWords.Length, "no two outcomes share a word");
        check(
            ActionOutcome.Completed.ReportsSuccess()
                && ActionOutcome.Verified.ReportsSuccess()
                && ActionOutcome.Dispatched.ReportsSuccess(),
            "successful outcomes");
        check(
            !ActionOutcome.Unknown.ReportsSuccess()
                && !ActionOutcome.Admitted.ReportsSuccess()
                && !ActionOutcome.PostconditionFailed.ReportsSuccess(),
            "uncertain outcomes are not successes");
    }

    private static void EveryErrorCodeHasItsWord(Action<bool, string> check)
    {
        check(ErrorCodeWords.Length == Enum.GetValues<ActionErrorCode>().Length, "the golden list names every error code");
        foreach ((ActionErrorCode code, string word) in ErrorCodeWords)
        {
            check(code.ToWireName() == word, "wire word of " + code);
        }
        check(ErrorCodeWords.Select(entry => entry.Word).Distinct().Count() == ErrorCodeWords.Length, "no two codes share a word");
        var result = new JsonObject { ["code"] = "consent_required" };
        check(
            ActionErrorCodeNames.Is(result, ActionErrorCode.ConsentRequired)
                && !ActionErrorCodeNames.Is(result, ActionErrorCode.ConsentDenied),
            "comparing a result with a code");
        check(!ActionErrorCodeNames.Is(new JsonObject(), ActionErrorCode.ConsentRequired), "a result without a code matches none");
    }

    private static void UnrecognizedOutcomeBecomesUnknown(Action<bool, string> check)
    {
        var reported = new List<string>();
        check(ActionOutcomeNames.Parse("teleported", reported.Add) == ActionOutcome.Unknown, "an unrecognized word is read as unknown");
        check(reported.SequenceEqual(["teleported"]), "the unrecognized word is reported");
        check(
            ActionOutcomeNames.Parse(null, reported.Add) == ActionOutcome.Unknown && reported.Count == 1,
            "a missing word is unknown and not reported");
        check(ActionOutcomeNames.Read(new JsonObject(), reported.Add) == ActionOutcome.Unknown, "a result without an outcome is unknown");
        check(ActionOutcomeNames.Parse("Verified") == ActionOutcome.Unknown, "the words are case sensitive");
    }

    private static void FailureResultsKeepTheirShape(Action<bool, string> check)
    {
        JsonObject rejected = ActionResults.Rejected(ActionErrorCode.Busy, "queue full");
        check(rejected["success"]!.GetValue<bool>() == false
            && rejected["code"]!.ToString() == "busy", "rejection carries success and code");
        check(
            rejected["outcome"]!.ToString() == "not_dispatched" && rejected["error"]!.ToString() == "queue full",
            "rejection is not dispatched and carries the message");
        JsonObject uncertain = ActionResults.Failure(ActionErrorCode.TransportError, ActionOutcome.Unknown, "lost");
        check(
            uncertain["outcome"]!.ToString() == "unknown" && uncertain["code"]!.ToString() == "transport_error",
            "failure carries both words");
        JsonObject withoutCode = ActionResults.Failure(ActionOutcome.NotDispatched, "offline");
        check(withoutCode["code"] == null && withoutCode["outcome"]!.ToString() == "not_dispatched", "failure without a code has none");
    }

    // A restart must not turn "may have run" into "did not run": a journal entry that stops at
    // admitted comes back as unknown, and a word nobody knows is never read as success.
    private static void JournalRecoveryReadsEveryWrittenWord(Action<bool, string> check, string temporaryRoot)
    {
        string directory = Path.Combine(temporaryRoot, "outcome-recovery");
        Directory.CreateDirectory(directory);
        var journal = new AuditJournal(directory);
        foreach ((ActionOutcome outcome, string word) in OutcomeWords)
        {
            journal.Append(new JsonObject { ["operation_key"] = "operator:" + word, ["request_hash"] = "hash", ["outcome"] = word });
        }
        journal.Append(new JsonObject { ["operation_key"] = "operator:invented", ["request_hash"] = "hash", ["outcome"] = "teleported" });
        var restarted = new AuditJournal(directory);
        foreach ((ActionOutcome outcome, string word) in OutcomeWords)
        {
            check(restarted.Operations.TryGetValue("operator:" + word, out var recovered), "recovered " + word);
            ActionOutcome expectedOutcome = outcome == ActionOutcome.Admitted ? ActionOutcome.Unknown : outcome;
            check(recovered!.Result["outcome"]!.ToString() == expectedOutcome.ToWireName(), "recovered outcome of " + word);
            check(recovered.Result["success"]!.GetValue<bool>() == outcome.ReportsSuccess(), "recovered success flag of " + word);
        }
        restarted.Operations.TryGetValue("operator:invented", out var invented);
        check(
            invented!.Result["outcome"]!.ToString() == "unknown" && !invented.Result["success"]!.GetValue<bool>(),
            "an invented word recovers as unknown, never as success");
    }
}
