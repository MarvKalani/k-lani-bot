namespace KLaniBot.Platforms.MacOS;

// The macOS tree reader and the actionability check classify each attribute they copy from an accessibility element,
// and an absent AXEnabled counts as enabled. Some elements answer a purely descriptive attribute with a generic
// failure, and that must not refuse a whole observation. A structural attribute that fails leaves the tree incomplete,
// so it stays a failure and the observation is refused.
public static class MacAttributeRead
{
    private const int NoError = 0;
    private const int AttributeUnsupported = -25205;
    private const int NoValue = -25212;
    private const int GenericFailure = -25200;

    private static readonly HashSet<string> DescriptiveAttributes = new(StringComparer.Ordinal)
    {
        "AXTitle", "AXDescription", "AXIdentifier", "AXSubrole", "AXValue", "AXHelp", "AXEnabled", "AXPosition", "AXSize"
    };

    // Maps the error code of one attribute copy to its outcome. Only a generic failure on a descriptive attribute is
    // absent; every other error stays a failure, including a generic failure on a structural attribute.
    public static MacAttributeOutcome Classify(string attributeName, int errorCode)
    {
        if (errorCode == NoError)
        {
            return MacAttributeOutcome.Value;
        }

        bool isMissingValue = errorCode is AttributeUnsupported or NoValue;
        bool isDescriptiveFailure = errorCode == GenericFailure && DescriptiveAttributes.Contains(attributeName);

        return isMissingValue || isDescriptiveFailure ? MacAttributeOutcome.Absent : MacAttributeOutcome.Failed;
    }

    // Null means the enabled attribute is absent or not a boolean. Text areas on macOS report no AXEnabled at all, so only
    // an explicit false disables an element.
    public static bool EnabledFrom(bool? reportedValue)
    {
        return reportedValue != false;
    }
}
