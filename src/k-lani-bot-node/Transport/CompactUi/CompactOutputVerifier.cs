using System.Diagnostics;

namespace KLaniBot.Transport.CompactUi;

// Routine execution verifies complete native output values before any compact response may truncate them.
public static class CompactOutputVerifier
{
    public static bool TryVerify(IReadOnlyList<CompactRecipeOutput> contract, IReadOnlyDictionary<string, string> parameters,
        IReadOnlyList<CompactTreeNode> before, IReadOnlyList<CompactTreeNode> after, bool legacyBound,
        out CompactCaptureAttempt<IReadOnlyDictionary<string, string>> attempt)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CompactRecipeOutput output in contract)
        {
            if (!TryValue(output, parameters, before, after, legacyBound, out var value))
            {
                attempt = new(null, value.Error);
                return false;
            }
            values[output.Name] = value.Value!;
        }
        attempt = new(values);

        return true;
    }

    private static bool TryValue(CompactRecipeOutput output, IReadOnlyDictionary<string, string> parameters,
        IReadOnlyList<CompactTreeNode> before, IReadOnlyList<CompactTreeNode> after, bool legacyBound,
        out CompactCaptureAttempt<string> attempt)
    {
        if (!CompactCapturedOutputs.TryRequired(after, output.Selector, output.Field, out var current))
        {
            attempt = new(null, current.Error);
            return false;
        }
        if (!CompactCapturedOutputs.TryRead(before, output.Selector, output.Field, out var previous))
        {
            attempt = new(null, previous.Error);
            return false;
        }
        return TryCurrent(output, parameters, previous.Value!, current.Value!, legacyBound, out attempt);
    }

    public static bool TryCurrent(CompactRecipeOutput output, IReadOnlyDictionary<string, string> parameters,
        CompactOptionalOutput previous, CompactOptionalOutput current, bool legacyBound, out CompactCaptureAttempt<string> attempt)
    {
        if (!current.Exists)
        {
            attempt = new(null, CompactCaptureError.OutputAbsent);
            return false;
        }
        string? value = current.Value;
        if (!Satisfies(output, parameters, value, previous.Value))
        {
            attempt = new(null, CompactCaptureError.PostconditionFailed);
            return false;
        }
        if (value == null || legacyBound && value.Length > CompactCaptureCodec.MaximumTextCharacters)
        {
            attempt = new(null, CompactCaptureError.OutputBoundExceeded);
            return false;
        }
        attempt = new(value);

        return true;
    }

    private static bool Satisfies(CompactRecipeOutput output, IReadOnlyDictionary<string, string> parameters, string? value, string? before)
    {
        return output.Check switch
        {
            CompactOutputCheckKind.Changed => value != before,
            CompactOutputCheckKind.EqualsParameter => output.Parameter != null
                && parameters.TryGetValue(output.Parameter, out string? parameter)
                && value == parameter,
            CompactOutputCheckKind.Exists => true,
            CompactOutputCheckKind.NotEmpty => CompactCapturedText.HasContent(value),
            _ => throw new UnreachableException()
        };
    }
}
