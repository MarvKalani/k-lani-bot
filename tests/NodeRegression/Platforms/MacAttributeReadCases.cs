using KLaniBot.Platforms.MacOS;

// The macOS tree reader treats a generic failure as absent only for a descriptive attribute, refuses the observation for
// a structural one, and reads an absent enabled attribute as enabled. The rules are pure, so they are checked on every
// platform; the live accessibility reads need a Mac.
internal static class MacAttributeReadCases
{
    public static int Run()
    {
        var checks = new Checks("Mac attribute read");
        CheckDescriptiveFailure(checks);
        CheckStructuralFailure(checks);
        CheckMissingValue(checks);
        CheckOtherErrors(checks);
        CheckSuccess(checks);
        CheckEnabled(checks);

        return checks.Passed;
    }

    private static void CheckEnabled(Checks checks)
    {
        checks.That(MacAttributeRead.EnabledFrom(null), "an absent enabled attribute is enabled");
        checks.That(MacAttributeRead.EnabledFrom(true), "an explicit true enabled attribute is enabled");
        checks.That(!MacAttributeRead.EnabledFrom(false), "an explicit false enabled attribute is disabled");
    }

    private static void CheckDescriptiveFailure(Checks checks)
    {
        checks.That(MacAttributeRead.Classify("AXDescription", -25200) == MacAttributeOutcome.Absent,
            "a generic failure on a description is absent");
        checks.That(MacAttributeRead.Classify("AXTitle", -25200) == MacAttributeOutcome.Absent,
            "a generic failure on a title is absent");
        checks.That(MacAttributeRead.Classify("AXIdentifier", -25200) == MacAttributeOutcome.Absent,
            "a generic failure on an identifier is absent");
    }

    private static void CheckStructuralFailure(Checks checks)
    {
        checks.That(MacAttributeRead.Classify("AXRole", -25200) == MacAttributeOutcome.Failed,
            "a generic failure on a role refuses the observation");
        checks.That(MacAttributeRead.Classify("AXChildren", -25200) == MacAttributeOutcome.Failed,
            "a generic failure on children refuses the observation");
        checks.That(MacAttributeRead.Classify("AXWindows", -25200) == MacAttributeOutcome.Failed,
            "a generic failure on windows refuses the observation");
    }

    private static void CheckMissingValue(Checks checks)
    {
        checks.That(MacAttributeRead.Classify("AXRole", -25205) == MacAttributeOutcome.Absent,
            "an unsupported structural attribute is absent");
        checks.That(MacAttributeRead.Classify("AXChildren", -25212) == MacAttributeOutcome.Absent,
            "a structural attribute without value is absent");
        checks.That(MacAttributeRead.Classify("AXDescription", -25205) == MacAttributeOutcome.Absent,
            "an unsupported description is absent");
    }

    private static void CheckOtherErrors(Checks checks)
    {
        checks.That(MacAttributeRead.Classify("AXDescription", -25211) == MacAttributeOutcome.Failed,
            "a disabled API refuses a description read");
        checks.That(MacAttributeRead.Classify("AXDescription", -25204) == MacAttributeOutcome.Failed,
            "a read that cannot complete refuses a description read");
        checks.That(MacAttributeRead.Classify("AXDescription", -25202) == MacAttributeOutcome.Failed,
            "an invalid element refuses a description read");
    }

    private static void CheckSuccess(Checks checks)
    {
        checks.That(MacAttributeRead.Classify("AXTitle", 0) == MacAttributeOutcome.Value, "a successful read is a value");
        checks.That(MacAttributeRead.Classify("AXRole", 0) == MacAttributeOutcome.Value,
            "a successful structural read is a value");
    }
}
