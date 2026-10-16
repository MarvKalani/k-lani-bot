using System.Text.Json.Nodes;

namespace KLaniBot.Advisory;

// Use case: some questions cannot be settled by a deterministic check. "Does this operation fit the task the
// operator approved?" or "does the evidence support the goal?" can be put to a classifier. The node asks it
// through this interface and never depends on which product answers, so the provider can be exchanged
// when a better one exists without touching the dispatcher, the gateway or the evaluation runner.
//
// What every provider must keep, because the callers rely on it:
// - The answer is advice. It never grants a permission and never overrides a deterministic check;
//   AdvisoryResult.ToJson states both as fixed false.
// - Doubt means abstaining: an unreachable service, a malformed answer, truncated input or a missing
//   calibration must surface as Uncertain or as AbstainFromAutomaticVerdict, never as agreement.
// - Questions and answers follow AdvisoryQuestionSchema, so callers can read every provider the same way.
public interface IAdvisoryEvaluator : IDisposable
{
    // Short stable name that is recorded with every answer, so evidence shows who advised.
    string ProviderId
    {
        get;
    }

    Task<AdvisoryResult> EvaluateAsync(JsonNode state, JsonObject questions, CancellationToken cancellation = default);
}
