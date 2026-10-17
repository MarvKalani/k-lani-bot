namespace KLaniBot.Transport.CompactUi;

// What a function stored for another build needs before it may serve the current build. No value means reusable: only an
// accepted test run or a repaired, newly qualified cartridge makes a function callable for the new build.
public enum CompactDriftStatus
{
    TestRunNeeded,
    RepairNeeded,
    Unchecked
}

// Why one stored step could not be confirmed in the current window. The window reasons describe application state and leave the
// function unchecked; the target reasons mean that the stored selector no longer names one control and the step needs repair.
public enum CompactDriftReason
{
    WindowAbsent,
    WindowAmbiguous,
    WindowScopeUnproved,
    UnnamedTarget,
    TargetAbsentOrAmbiguous,
    TargetTypeChanged
}

// The first stored step that did not resolve, with its position in the recipe.
public sealed record CompactDriftStep(int Index, CompactRecipeStep Step, CompactDriftReason Reason);

// One function of another build against the current window. RunOnlySteps counts reads after a mutation, which only a test run can check.
public sealed record CompactDriftFunction(string Name, CompactDriftStatus Status, CompactDriftStep? Step, int CheckedSteps,
    int RunOnlySteps);

// The functions of one other build of the same program variant, assessed against the current build's window.
public sealed record CompactBuildDriftReport(CompactProgramIdentity Source, CompactProgramIdentity Current,
    IReadOnlyList<CompactDriftFunction> Functions);

// The current application instance as the probe sees it: its windows and one tree per window kind that could be read.
public sealed record CompactDriftView(CompactNativeWindow Instance, IReadOnlyList<CompactNativeWindow> Windows,
    IReadOnlyDictionary<CompactWindowKind, CompactNativeObservation> Trees);

// Use case: ui_info sees a known build that has no qualified cartridge while another build of the same variant has one. This probe
// applies the target rule of Info.preflight to each stored step without input, clicks or build check, so the agent learns which
// functions need only a test run and which step of which function it has to explore and repair.
public static class CompactBuildDriftProbe
{
    public static CompactDriftFunction Assess(CompactFunctionDraft function, CompactDriftView view)
    {
        CompactDriftStep? firstWindowProblem = null;
        int checkedSteps = 0;
        int runOnlySteps = 0;
        bool mutated = false;
        for (int index = 0; index < function.Steps.Count; index++)
        {
            CompactRecipeStep step = function.Steps[index];
            bool runOnly = step.Action == CompactRecipeAction.Read && mutated;
            mutated |= step.Action != CompactRecipeAction.Read;
            if (runOnly)
            {
                runOnlySteps++;
                continue;
            }
            CompactDriftReason? reason = StepReason(step, view);
            if (reason == null)
            {
                checkedSteps++;
                continue;
            }
            var problem = new CompactDriftStep(index, step, reason.Value);
            if (NeedsRepair(reason.Value))
            {
                return new CompactDriftFunction(function.Name, CompactDriftStatus.RepairNeeded, problem, checkedSteps, runOnlySteps);
            }
            firstWindowProblem ??= problem;
        }
        CompactDriftStatus status = firstWindowProblem == null ? CompactDriftStatus.TestRunNeeded : CompactDriftStatus.Unchecked;

        return new CompactDriftFunction(function.Name, status, firstWindowProblem, checkedSteps, runOnlySteps);
    }

    // The window kinds whose trees the probe reads: those of every step it checks before running.
    public static IReadOnlySet<CompactWindowKind> CheckedKinds(IEnumerable<CompactFunctionDraft> functions)
    {
        var kinds = new HashSet<CompactWindowKind>();
        foreach (CompactFunctionDraft function in functions)
        {
            bool mutated = false;
            foreach (CompactRecipeStep step in function.Steps)
            {
                if (step.Action != CompactRecipeAction.Read || !mutated)
                {
                    kinds.Add(step.Selector.WindowKind);
                }
                mutated |= step.Action != CompactRecipeAction.Read;
            }
        }

        return kinds;
    }

    // Null when the step's target is uniquely present in the one current window of its kind.
    private static CompactDriftReason? StepReason(CompactRecipeStep step, CompactDriftView view)
    {
        CompactWindowKind kind = step.Selector.WindowKind;
        int windows = view.Windows.Count(window => window.ProcessId == view.Instance.ProcessId
            && window.Started == view.Instance.Started && window.Kind == kind);
        if (windows == 0)
        {
            return CompactDriftReason.WindowAbsent;
        }
        if (windows > 1)
        {
            return CompactDriftReason.WindowAmbiguous;
        }
        if (!view.Trees.TryGetValue(kind, out var tree))
        {
            return CompactDriftReason.WindowScopeUnproved;
        }
        if (CompactRecipeTarget.TryPresent(tree, step.Selector, view.Instance, step.Action, out var failure))
        {
            return null;
        }

        return Reason(failure!);
    }

    private static CompactDriftReason Reason(CompactTargetFailure failure)
    {
        switch (failure.Kind)
        {
            case CompactTargetFailureKind.UnnamedRefused:
                return CompactDriftReason.UnnamedTarget;
            case CompactTargetFailureKind.Tree:
                return TreeReason(failure.Tree!.Value);
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    // A presence check never reports a disabled target; scope errors describe the window, not the stored selector.
    private static CompactDriftReason TreeReason(CompactTreeError error)
    {
        switch (error)
        {
            case CompactTreeError.UnsupportedFormat:
            case CompactTreeError.RootAmbiguousOrAbsent:
            case CompactTreeError.WindowIdentityChanged:
                return CompactDriftReason.WindowScopeUnproved;
            case CompactTreeError.TargetAbsentOrAmbiguous:
                return CompactDriftReason.TargetAbsentOrAmbiguous;
            case CompactTreeError.TargetTypeChanged:
                return CompactDriftReason.TargetTypeChanged;
            case CompactTreeError.TargetDisabled:
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    private static bool NeedsRepair(CompactDriftReason reason)
    {
        switch (reason)
        {
            case CompactDriftReason.WindowAbsent:
            case CompactDriftReason.WindowAmbiguous:
            case CompactDriftReason.WindowScopeUnproved:
                return false;
            case CompactDriftReason.UnnamedTarget:
            case CompactDriftReason.TargetAbsentOrAmbiguous:
            case CompactDriftReason.TargetTypeChanged:
                return true;
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }
}
