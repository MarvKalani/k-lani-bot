namespace KLaniBot.Platforms;

// Who calls it: the Windows UI backend, to say whether a window cloaked by DWM can be brought forward. Unknown is kept apart
// from NotCloaked, because a window whose cloak state cannot be read is refused like a cloaked one.
public enum WindowCloakState
{
    NotCloaked,
    Cloaked,
    Unknown
}

// Who calls it: the Windows UI backend, to record what one activation attempt achieved. NotAttempted means the step did not run.
public enum ForegroundAttemptOutcome
{
    NotAttempted,
    Succeeded,
    Failed
}

// Who calls it: the Windows UI backend, which executes the step the plan orders and reports the next one back. Only
// AlreadyForeground is success. RestoreAndActivate and AttachAndActivate are the two attempts; GiveUp ends the activation.
public enum ForegroundActivationStep
{
    AlreadyForeground,
    RestoreAndActivate,
    AttachAndActivate,
    GiveUp,
    RefuseCloaked
}

// Who calls it: the Windows UI backend, with the facts it observed about the top-level window before the next step. The
// attempt outcomes are recorded after each step, so the plan never orders the same attempt twice.
public sealed record ForegroundActivationFacts(
    WindowCloakState CloakState,
    bool IsForeground,
    bool IsMinimized,
    ForegroundAttemptOutcome RestoreAttempt,
    ForegroundAttemptOutcome AttachAttempt);

// Who calls it: the Windows UI backend, before and after every activation step. It decides from observed facts only and
// sends no input, so the decision table runs on every platform.
public static class ForegroundActivationPlan
{
    // A cloaked or unknown window is refused first, because such a window is not brought forward at all.
    public static ForegroundActivationStep NextStep(ForegroundActivationFacts facts)
    {
        if (facts.CloakState != WindowCloakState.NotCloaked)
        {
            return ForegroundActivationStep.RefuseCloaked;
        }

        if (facts.IsForeground)
        {
            return ForegroundActivationStep.AlreadyForeground;
        }

        if (facts.RestoreAttempt == ForegroundAttemptOutcome.NotAttempted)
        {
            return ForegroundActivationStep.RestoreAndActivate;
        }

        if (facts.AttachAttempt == ForegroundAttemptOutcome.NotAttempted)
        {
            return ForegroundActivationStep.AttachAndActivate;
        }

        return ForegroundActivationStep.GiveUp;
    }

    // A window that is not minimized keeps its size and position; only a minimized window is restored before activation.
    public static bool ShouldRestoreBeforeActivation(ForegroundActivationFacts facts)
    {
        return facts.IsMinimized;
    }
}
