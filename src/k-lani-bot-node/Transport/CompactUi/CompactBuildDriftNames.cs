using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Use case: ui_info shows a build-drift report on the row of the instance it concerns, and the full per-step detail goes to the
// result store. The row names functions and the step to repair; it never says reusable or compatible.
public static class CompactBuildDriftNames
{
    // Each list on a row names this many functions; a count of the rest keeps the reply small.
    public const int FunctionLimit = CompactFunctionSummary.Limit;

    // The reply sends this after the rows when a row carries a report, so the agent knows what each list asks of it.
    public const string Hint = "other_builds lists stored functions of another build of the same program, probed against this window "
        + "without input. None is callable here: test_run_needed means every stored target still resolves, so test the function "
        + "in an authorized fixture; repair_needed names the first step whose target no longer resolves, so start exploring at "
        + "that step; unchecked names a window that must be open first. Details: cartridge_call Info.result {evidence_ref, "
        + "section: \"debug\"}.";

    public static string Status(CompactDriftStatus status)
    {
        return status switch
        {
            CompactDriftStatus.TestRunNeeded => "test_run_needed",
            CompactDriftStatus.RepairNeeded => "repair_needed",
            CompactDriftStatus.Unchecked => "unchecked",
            _ => throw new UnreachableException()
        };
    }

    public static string Reason(CompactDriftReason reason)
    {
        return reason switch
        {
            CompactDriftReason.WindowAbsent => "window_absent",
            CompactDriftReason.WindowAmbiguous => "window_ambiguous",
            CompactDriftReason.WindowScopeUnproved => "window_scope_unproved",
            CompactDriftReason.UnnamedTarget => "unnamed_target",
            CompactDriftReason.TargetAbsentOrAmbiguous => "target_absent_or_ambiguous",
            CompactDriftReason.TargetTypeChanged => "target_type_changed",
            _ => throw new UnreachableException()
        };
    }

    // The row entry of one other build. Generated names stay out of what the model reads, as in the function listing.
    public static JsonObject Row(CompactBuildDriftReport report, string evidence)
    {
        var listed = report.Functions.Where(function => !CompactAutomaticNames.IsGenerated(function.Name)).ToArray();
        var row = new JsonObject { ["build"] = report.Source.Build };
        foreach (CompactDriftStatus status in Enum.GetValues<CompactDriftStatus>())
        {
            var functions = listed.Where(function => function.Status == status).ToArray();
            if (functions.Length == 0)
            {
                continue;
            }
            row[Status(status)] = new JsonArray(functions.Take(FunctionLimit).Select(RowEntry).ToArray());
            if (functions.Length > FunctionLimit)
            {
                row["more_" + Status(status)] = functions.Length - FunctionLimit;
            }
        }
        row["evidence_ref"] = evidence;

        return row;
    }

    // A function that needs only a test run is named alone; the others name their first open step.
    private static JsonNode? RowEntry(CompactDriftFunction function)
    {
        if (function.Step == null)
        {
            return JsonValue.Create(function.Name);
        }
        var entry = new JsonObject
        {
            ["name"] = function.Name,
            ["step"] = function.Step.Index + 1,
            ["label"] = CompactStepLabel.Of(function.Step.Step),
            ["reason"] = Reason(function.Step.Reason)
        };
        if (function.Status == CompactDriftStatus.Unchecked)
        {
            entry["window"] = CompactWindowKindWords.Text(function.Step.Step.Selector.WindowKind);
        }

        return entry;
    }

    // The private detail of every report of one instance: each function, generated ones included, with its stored selector.
    public static JsonObject Detail(IReadOnlyList<CompactBuildDriftReport> reports)
    {
        return new JsonObject
        {
            ["kind"] = "build_drift_probe",
            ["input_sent"] = false,
            ["current"] = Identity(reports[0].Current),
            ["builds"] = new JsonArray(reports.Select(report => (JsonNode?)new JsonObject
            {
                ["source"] = Identity(report.Source),
                ["functions"] = new JsonArray(report.Functions.Select(function => (JsonNode?)FunctionDetail(function)).ToArray())
            }).ToArray())
        };
    }

    private static JsonObject FunctionDetail(CompactDriftFunction function)
    {
        var detail = new JsonObject
        {
            ["name"] = function.Name,
            ["status"] = Status(function.Status),
            ["checked_steps"] = function.CheckedSteps,
            ["test_run_only_steps"] = function.RunOnlySteps
        };
        if (function.Step != null)
        {
            CompactStoredSelector selector = function.Step.Step.Selector;
            detail["step"] = new JsonObject
            {
                ["number"] = function.Step.Index + 1,
                ["action"] = CompactCaptureNames.Action(function.Step.Step.Action),
                ["label"] = CompactStepLabel.Of(function.Step.Step),
                ["window"] = CompactWindowKindWords.Text(selector.WindowKind),
                ["role"] = selector.Role.Name,
                ["name"] = selector.Name,
                ["automation_id"] = selector.AutoId,
                ["reason"] = Reason(function.Step.Reason)
            };
        }

        return detail;
    }

    private static JsonObject Identity(CompactProgramIdentity identity)
    {
        return new JsonObject { ["key"] = identity.Key, ["build"] = identity.Build };
    }
}
