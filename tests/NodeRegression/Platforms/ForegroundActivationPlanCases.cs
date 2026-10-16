using KLaniBot.Platforms;

// The Windows backend orders its activation attempts with a pure plan, so the decision table is checked on every platform.
internal static class ForegroundActivationPlanCases
{
    public static int Run()
    {
        var checks = new Checks("Foreground activation plan");
        RunRefusalCases(checks);
        RunSuccessCases(checks);
        RunAttemptCases(checks);
        RunRestoreCases(checks);

        return checks.Passed;
    }

    private static void RunRefusalCases(Checks checks)
    {
        checks.That(
            Next(Observed(WindowCloakState.Cloaked)) == ForegroundActivationStep.RefuseCloaked,
            "a cloaked window is refused before any attempt");
        checks.That(
            Next(Observed(WindowCloakState.Cloaked, isForeground: true)) == ForegroundActivationStep.RefuseCloaked,
            "the cloak check comes before the foreground check");
        checks.That(
            Next(Observed(WindowCloakState.Unknown)) == ForegroundActivationStep.RefuseCloaked,
            "an unknown cloak state is refused like a cloaked window");
    }

    private static void RunSuccessCases(Checks checks)
    {
        checks.That(
            Next(Observed(isForeground: true)) == ForegroundActivationStep.AlreadyForeground,
            "a window that is already in front needs no attempt");
        // The top-level dialog resolves to itself under GA_ROOT, so the foreground comparison matches it directly.
        checks.That(
            Next(Observed(isForeground: true, isMinimized: false)) == ForegroundActivationStep.AlreadyForeground,
            "foreground is already the target's own top-level dialog");
        checks.That(
            Next(Observed(isForeground: true, restoreAttempt: ForegroundAttemptOutcome.Succeeded))
                == ForegroundActivationStep.AlreadyForeground,
            "a successful restore-and-activate ends as already foreground");
        checks.That(
            Next(Observed(
                isForeground: true,
                restoreAttempt: ForegroundAttemptOutcome.Failed,
                attachAttempt: ForegroundAttemptOutcome.Succeeded)) == ForegroundActivationStep.AlreadyForeground,
            "a successful attach-and-activate ends as already foreground");
    }

    private static void RunAttemptCases(Checks checks)
    {
        checks.That(
            Next(Observed()) == ForegroundActivationStep.RestoreAndActivate,
            "the first step restores and activates");
        checks.That(
            Next(Observed(restoreAttempt: ForegroundAttemptOutcome.Failed)) == ForegroundActivationStep.AttachAndActivate,
            "a failed restore-and-activate falls back to attaching");
        checks.That(
            Next(Observed(
                restoreAttempt: ForegroundAttemptOutcome.Failed,
                attachAttempt: ForegroundAttemptOutcome.Failed)) == ForegroundActivationStep.GiveUp,
            "a failed attach gives up without any further input");
    }

    private static void RunRestoreCases(Checks checks)
    {
        checks.That(
            ForegroundActivationPlan.ShouldRestoreBeforeActivation(Observed(isMinimized: true)),
            "a minimized window is restored before activation");
        checks.That(
            !ForegroundActivationPlan.ShouldRestoreBeforeActivation(Observed(isMinimized: false)),
            "a window that is not minimized keeps its size and position");
    }

    private static ForegroundActivationStep Next(ForegroundActivationFacts facts)
    {
        return ForegroundActivationPlan.NextStep(facts);
    }

    private static ForegroundActivationFacts Observed(
        WindowCloakState cloakState = WindowCloakState.NotCloaked,
        bool isForeground = false,
        bool isMinimized = false,
        ForegroundAttemptOutcome restoreAttempt = ForegroundAttemptOutcome.NotAttempted,
        ForegroundAttemptOutcome attachAttempt = ForegroundAttemptOutcome.NotAttempted)
    {
        return new ForegroundActivationFacts(cloakState, isForeground, isMinimized, restoreAttempt, attachAttempt);
    }
}
