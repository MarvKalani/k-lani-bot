using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Routine workflow replies expose progress and decision summaries; complete values/operation traces remain private evidence.
public static class CompactWorkflowProgressWire
{
    public static JsonObject Reply(CompactWorkflowProgress progress)
    {
        var reply = new JsonObject
        {
            ["success"] = true,
            ["job_ref"] = progress.Reference.Digest.Hex,
            ["status"] = CompactWorkflowJobNames.Status(progress.Status),
            ["revision"] = progress.Revision,
            ["completed_stages"] = new JsonArray(progress.CompletedStages.Select(stage => (JsonNode?)JsonValue.Create(stage)).ToArray()),
            ["next_stage"] = progress.NextStage
        };
        if (progress.Decision != null)
        {
            reply["decision"] = new JsonObject { ["input"] = progress.Decision.Input, ["prompt"] = progress.Decision.Prompt };
        }
        if (progress.Error != null)
        {
            reply["error"] = progress.Error;
        }

        return reply;
    }
}
