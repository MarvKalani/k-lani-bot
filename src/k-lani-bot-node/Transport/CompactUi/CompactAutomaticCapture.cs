using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// One installed function combines a typed recipe with its validated legacy or modern compatibility evidence.
public sealed record CompactInstalledFunction(CompactFunctionDraft Draft, CompactRecordedCompatibility Compatibility);

// Unverified material contains no training text and is never exposed as a callable function.
public sealed record CompactCaptureCandidate(string Name, CompactCartridgeTopic Topic, CompactRecipeAction Action,
    CompactStoredSelector Selector, string Reason);

// The library must finish trusted compatibility capture and atomic installation before automatic capture reports available.
public interface ICompactCaptureLibrary
{
    IReadOnlyList<CompactInstalledFunction> Functions
    {
        get;
    }
    bool TryInstall(CompactFunctionDraft draft, CompactCompatibilityContract compatibility, out string? error);
    bool TryCandidate(CompactCaptureCandidate candidate, out string? error);
}

// Automatic first-use outcomes distinguish verified installation, material needing a goal check and storage failure.
public enum CompactAutomaticStatus
{
    Available,
    NeedsVerification,
    StorageFailed
}

// A compact result reports only newly installed functions or failure reasons; detailed candidate material is optional.
public sealed record CompactAutomaticResult(string Name, CompactAutomaticStatus Status, bool Verified = false,
    bool Reused = false, string? Reason = null, CompactCaptureCandidate? Material = null);

// First-use capture compiles locally from the current successful demonstration without extra application input or authoring model calls.
public sealed class CompactAutomaticCapture(ICompactCaptureLibrary library)
{
    public IReadOnlyList<CompactAutomaticResult> Capture(CompactDemonstration demonstration,
        IReadOnlyDictionary<string, CompactSemanticTarget> references, CompactObservedCompatibility current,
        CompactCartridgeTopic topic, bool compact)
    {
        var results = new List<CompactAutomaticResult>();
        for (int index = 0; index < demonstration.Steps.Count; index++)
        {
            CompactAutomaticResult? result = CaptureStep(demonstration, index, references, current, topic, compact);
            if (result != null)
            {
                results.Add(result);
            }
        }

        return results;
    }

    private CompactAutomaticResult? CaptureStep(CompactDemonstration demonstration, int index,
        IReadOnlyDictionary<string, CompactSemanticTarget> references, CompactObservedCompatibility current,
        CompactCartridgeTopic topic, bool compact)
    {
        CompactDemonstrationStep step = demonstration.Steps[index];
        // A focused key step has no stored form yet, so it is never captured as a plain click.
        if (step.Action == CompactRecipeAction.Key)
        {
            return null;
        }
        if (step.Target == null || !CompactAutomaticNames.TryName(step.Target, step.Action, out string? name))
        {
            return null;
        }
        if (step.Target.ProcessId != demonstration.ProcessId)
        {
            return new(name!, CompactAutomaticStatus.NeedsVerification,
                Reason: CompactCaptureNames.Error(CompactCaptureError.ForeignReference));
        }
        CompactInstalledFunction? existing = library.Functions.FirstOrDefault(entry => entry.Draft.Name == name
            || CompactAutomaticNames.IsGenerated(entry.Draft.Name) && Matches(entry.Draft, step));
        if (existing != null)
        {
            CompactCompatibilityVerdict verdict = CompactCompatibility.Assess(existing.Draft, existing.Compatibility, current);
            if (verdict.Status != CompactCompatibilityStatus.Reusable)
            {
                string reason = "Generated function compatibility changed: "
                    + CompactCompatibilityNames.Verdict(verdict)["reason"]!.GetValue<string>() + "; revalidate before input.";
                return Candidate(name!, step, topic, reason, compact);
            }
            if (!Matches(existing.Draft, step))
            {
                return Candidate(name!, step, topic, CompactCaptureNames.Error(CompactCaptureError.CapturedScopeChanged), compact);
            }
            return compact ? null : new(existing.Draft.Name, CompactAutomaticStatus.Available, Reused: true);
        }

        return Compile(demonstration, step, name!, references, current, topic, compact);
    }

    private CompactAutomaticResult Compile(CompactDemonstration demonstration, CompactDemonstrationStep step, string name,
        IReadOnlyDictionary<string, CompactSemanticTarget> references, CompactObservedCompatibility current,
        CompactCartridgeTopic topic, bool compact)
    {
        if (!TryOutput(step, references, out var output))
        {
            return Candidate(name, step, topic, CompactCaptureNames.Error(output.Error!.Value), compact);
        }
        var selected = demonstration with
        {
            Steps = [step]
        };
        var request = new CompactCaptureRequest(name, topic, step.Action == CompactRecipeAction.Write ? ["text"] : [], [output.Value!]);
        if (!CompactCaptureCompiler.TryCompile(selected, request, references, out var compiled))
        {
            return Candidate(name, step, topic, CompactCaptureNames.Error(compiled.Error!.Value), compact);
        }
        if (!CompactCompatibility.TryCapture(compiled.Value!, current, out var compatibility))
        {
            return Candidate(name, step, topic, CompactCompatibilityNames.Error(compatibility.Error!.Value), compact);
        }
        var recorded = new CompactRecordedCompatibility(CompactCompatibilityFormat.Contracts2, Contracts: compatibility.Value!);
        if (CompactCompatibility.Assess(compiled.Value!, recorded, current).Status != CompactCompatibilityStatus.Reusable)
        {
            return Candidate(name, step, topic, "Current application/node identity is unknown; observe Info before capture.", compact);
        }
        if (!library.TryInstall(compiled.Value!, compatibility.Value!, out string? error))
        {
            return Candidate(name, step, topic, error ?? "Function installation failed.", compact);
        }

        return new(name, CompactAutomaticStatus.Available, Verified: true);
    }

    private CompactAutomaticResult Candidate(string name, CompactDemonstrationStep step, CompactCartridgeTopic topic,
        string reason, bool compact)
    {
        if (!CompactCapturedSelectors.TryCreate(step.Target, step.Target!.ProcessId, out var selector)
            || step.Action is not CompactRecipeAction.Click and not CompactRecipeAction.Write)
        {
            return new(name, CompactAutomaticStatus.StorageFailed, Reason: "Invalid generated candidate.");
        }
        var candidate = new CompactCaptureCandidate(name, topic, step.Action, selector.Value!, reason);
        if (!library.Functions.Any(entry => entry.Draft.Name == name) && !library.TryCandidate(candidate, out string? error))
        {
            return new(name, CompactAutomaticStatus.StorageFailed, Reason: error);
        }

        return new(name, CompactAutomaticStatus.NeedsVerification, Reason: reason, Material: compact ? null : candidate);
    }

    private static bool TryOutput(CompactDemonstrationStep step, IReadOnlyDictionary<string, CompactSemanticTarget> references,
        out CompactCaptureAttempt<CompactCaptureOutput> attempt)
    {
        if (step.Action == CompactRecipeAction.Write)
        {
            attempt = step.Reference == null ? new(null, CompactCaptureError.UnknownReference)
                : new(new("value", step.Reference, CompactOutputField.Value));
            return attempt.Error == null;
        }
        if (step.Action != CompactRecipeAction.Click || !step.Target!.Role.Name.Contains("tab", StringComparison.OrdinalIgnoreCase))
        {
            attempt = new(null, CompactCaptureError.GoalOutputRequired);
            return false;
        }
        var matches = references.Where(entry => entry.Value.ProcessId == step.Target.ProcessId
            && entry.Value.WindowKind == step.Target.WindowKind && entry.Value.Role.Name == "heading"
            && entry.Value.Name == step.Target.Name).ToArray();
        attempt = matches.Length == 1 ? new(new("page", matches[0].Key, CompactOutputField.Name))
            : new(null, CompactCaptureError.NavigationHeadingAmbiguous);

        return attempt.Error == null;
    }

    private static bool Matches(CompactFunctionDraft function, CompactDemonstrationStep step)
    {
        return function.Steps.Count > 0 && CompactCapturedSelectors.Matches(function.Steps[0].Selector, step.Target!,
            step.Action, function.Steps[0].Action);
    }
}
