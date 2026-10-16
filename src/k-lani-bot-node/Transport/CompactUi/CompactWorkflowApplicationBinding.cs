namespace KLaniBot.Transport.CompactUi;

// Initial application selection requires a unique current variant/build; later operations retain the original process/window lifetime.
public static class CompactWorkflowApplicationBinding
{
    // Throws CompactNodeException for missing, ambiguous, unknown or incompatible application identity.
    public static CompactNativeWindow Select(IReadOnlyList<CompactNativeWindow> windows, CompactWorkflowApplication application,
        string? processName, CompactWorkflowInstance? previous)
    {
        if (string.IsNullOrEmpty(processName) || application.ProcessName != null && application.ProcessName != processName)
        {
            throw new CompactNodeException("Actual application variant mapping is unknown or changed.");
        }
        var candidates = windows.Where(window => previous == null
            ? window.ProcessName == processName && (application.WindowKind == null || window.Kind == application.WindowKind)
            : window.ProcessId == previous.ProcessId && window.Handle == previous.Handle && window.Started == previous.Started).ToArray();
        if (candidates.Length != 1 || string.IsNullOrEmpty(candidates[0].Started))
        {
            throw new CompactNodeException("Workflow application instance is absent, replaced or ambiguous.");
        }
        var current = candidates[0];
        if (current.ProcessName != processName || current.Build == "unknown" || current.Build != application.Identity.Build)
        {
            throw new CompactNodeException("Actual application variant/build does not match the selected cartridge.");
        }

        return current;
    }

    // Throws CompactNodeException for missing or non-text values; absent text never becomes an implicit empty-field replacement.
    public static IReadOnlyDictionary<string, string> Strings(IReadOnlyDictionary<string, CompactWorkflowValue> values)
    {
        if (values.Values.Any(value => value.Kind is not CompactWorkflowValueKind.String and not CompactWorkflowValueKind.Enum
            || value.Text == null))
        {
            throw new CompactNodeException("Native text functions require typed complete string parameters.");
        }

        return values.ToDictionary(entry => entry.Key, entry => entry.Value.Text!, StringComparer.Ordinal);
    }

    public static IReadOnlyDictionary<string, CompactWorkflowValue> Values(IReadOnlyDictionary<string, string> values)
    {
        return values.ToDictionary(entry => entry.Key,
            entry => new CompactWorkflowValue(CompactWorkflowValueKind.String, Text: entry.Value),
            StringComparer.Ordinal);
    }
}
