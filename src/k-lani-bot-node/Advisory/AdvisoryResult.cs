using System.Text.Json.Nodes;

namespace KLaniBot.Advisory;

// How an advisory provider's answer ended; only an Advisory answer that does not abstain may influence a decision.
public enum AdvisoryOutcome
{
    // The provider answered the questions. Whether the answer may be used is shown by AbstainFromAutomaticVerdict.
    Advisory,

    // The provider could not give a reliable answer (unreachable, malformed, truncated, deadline exceeded).
    Uncertain
}

// What an advisory evaluation returned, in a form callers can read without knowing the provider.
public sealed record AdvisoryResult(
    bool Success,
    AdvisoryOutcome Outcome,
    string RequestId,
    string ProviderId,
    string ModelRevision,
    string RubricSha256,
    JsonObject? Answers,
    JsonNode? Usage,
    string? Model,
    bool CalibrationValidated,
    bool AbstainFromAutomaticVerdict,
    string? Error)
{
    public static AdvisoryResult Uncertain(string requestId, string providerId, string modelRevision, string rubricSha256, string error)
    {
        return new AdvisoryResult(
            false,
            AdvisoryOutcome.Uncertain,
            requestId,
            providerId,
            modelRevision,
            rubricSha256,
            null,
            null,
            null,
            false,
            true,
            error);
    }

    // The label a choice question settled on, or null when the question was not answered.
    public string? ChosenLabel(string question)
    {
        return Answers?[question]?["choice"]?.ToString();
    }

    // The wire form (tool results, evaluation reports, audit). The two permission fields are constants here
    // and not provider output: no provider can claim authority over deterministic checks or permissions.
    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["success"] = Success,
            ["outcome"] = Outcome == AdvisoryOutcome.Advisory ? "advisory" : "uncertain",
            ["request_id"] = RequestId,
            ["provider"] = ProviderId
        };
        if (Answers != null)
        {
            json["answers"] = Answers.DeepClone();
            json["usage"] = Usage?.DeepClone();
            json["model"] = Model;
        }
        json["model_revision"] = ModelRevision;
        json["rubric_sha256"] = RubricSha256;
        if (Answers != null)
        {
            json["calibration_validated"] = CalibrationValidated;
            json["abstain_from_automatic_verdict"] = AbstainFromAutomaticVerdict;
        }
        else
        {
            json["error"] = Error;
        }
        json["can_override_assertions"] = false;
        json["can_grant_permissions"] = false;
        return json;
    }
}
