using System.Text.Json.Nodes;

namespace KLaniBot.Advisory;

// Use case: a classifier's answer only counts as an automatic verdict after the operator has checked that
// this model, on this rubric, is right often enough on cases it has not seen. The operator records that in
// a calibration report; this decides whether a report really covers the current model and questions.
//
// The report is bound to the model revision and to the hash of the questions. Changing either (for example
// when the provider is exchanged) makes the report fail, and every answer then abstains until a new report
// has been approved. That is intended: a calibration says nothing about a different model.
public static class AdvisoryCalibration
{
    public static bool Covers(string? reportFile, string modelRevision, string rubricSha256)
    {
        if (reportFile == null)
        {
            return false;
        }
        var report = JsonNode.Parse(File.ReadAllText(reportFile))!;
        bool sameModelAndRubric = report["model_revision"]?.ToString() == modelRevision
            && report["rubric_sha256"]?.ToString() == rubricSha256;
        bool heldOutData = report["held_out_dataset_sha256"]?.ToString().Length == 64 && report["sample_count"]?.GetValue<int>() > 0;
        bool approved = report["approved_by_operator"]?.GetValue<bool>() == true;
        return report["version"]?.GetValue<int>() == 1 && sameModelAndRubric && heldOutData && approved;
    }
}
