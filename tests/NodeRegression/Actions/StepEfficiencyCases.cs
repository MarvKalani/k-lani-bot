using KLaniBot.Localization;
using KLaniBot.Visibility;

// The retained step log must expose measured local work without inventing model usage or counting replayed entries twice.
internal static class StepEfficiencyCases
{
    public static int Run()
    {
        var checks = new Checks("Step efficiency");
        var first = Finished("one", StepStatus.Completed, 20, 40, 10);
        var failed = Finished("two", StepStatus.Failed, 30, 60, 15);
        var planned = Finished("future", StepStatus.Completed, 999, 999, 999);
        var steps = new StepList([first, failed], first, [planned], false);
        var measured = StepEfficiency.Measure(steps);
        checks.That(measured.Completed == 1 && measured.Failed == 1 && measured.Entries == 2,
            "replayed operation is counted once and planned work is excluded");
        checks.That(measured.ArgumentBytes == 50 && measured.ResultBytes == 100 && measured.ElapsedMilliseconds == 25,
            "actual retained diagnostics are aggregated including failed work");
        checks.That(measured.MeasuredEntries == 2, "coverage counts measured entries");
        var running = first with
        {
            Status = StepStatus.Running,
            Diagnostics = null
        };
        var reconciled = StepEfficiency.Measure(new([running], first, [], false));
        checks.That(reconciled.Completed == 1 && reconciled.MeasuredEntries == 1,
            "the latest state of a reconciled operation replaces its earlier incomplete entry");
        CheckUnknown(checks);
        CheckText(checks, steps);

        return checks.Passed;
    }

    private static void CheckUnknown(Checks checks)
    {
        var unknown = new StepView(StepKind.Read, "read", StepStatus.Uncertain, OperationId: "unknown");
        var invalid = Finished("invalid", StepStatus.Completed, -1, 20, 3);
        var result = StepEfficiency.Measure(new([invalid], unknown, [], false));
        checks.That(result.ArgumentBytes == null && result.ResultBytes == null && result.ElapsedMilliseconds == null,
            "missing or invalid measurements remain unknown rather than zero");
        checks.That(result.Uncertain == 1 && result.MeasuredEntries == 0 && result.Entries == 2,
            "uncertain outcomes and missing coverage remain visible");
        var large = Finished("large", StepStatus.Completed, long.MaxValue, 20, 3);
        result = StepEfficiency.Measure(new([large], Finished("next", StepStatus.Completed, 1, 2, 3), [], false));
        checks.That(result.ArgumentBytes == null, "overflow is unknown instead of wrapping or aborting the display");
    }

    private static void CheckText(Checks checks, StepList steps)
    {
        UiLanguage previous = UiTexts.Language;
        try
        {
            UiTexts.Use(UiLanguage.German);
            string text = StepEfficiencyText.Format(StepEfficiency.Measure(steps));
            checks.That(text.Contains("Modelltokens: nicht gemeldet", StringComparison.Ordinal),
                "no token estimate is derived from local JSON bytes");
            checks.That(text.Contains("Verlauf", StringComparison.Ordinal), "the measurement scope is the retained log");
            checks.That(!text.Contains("%", StringComparison.Ordinal), "no efficiency percentage without a matched baseline");
        }
        finally
        {
            UiTexts.Use(previous);
        }
    }

    private static StepView Finished(string id, StepStatus status, long input, long output, long milliseconds)
    {
        return new(StepKind.Read, "read", status, OperationId: id,
            Diagnostics: new("summary", "debug", "private content", input, output, milliseconds, DateTimeOffset.UnixEpoch));
    }
}
