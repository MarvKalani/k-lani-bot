namespace KLaniBot.Transport.CompactUi;

// Read capture and output verification preserve the JS reference's trim semantics rather than the broader .NET whitespace set.
public static class CompactCapturedText
{
    public static bool HasContent(string? value)
    {
        if (value == null)
        {
            return false;
        }
        foreach (char character in value)
        {
            if (!IsTrimmed(character))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTrimmed(char value)
    {
        return value is >= '\u0009' and <= '\u000D' or '\u0020' or '\u00A0' or '\u1680'
            or >= '\u2000' and <= '\u200A' or '\u2028' or '\u2029' or '\u202F' or '\u205F' or '\u3000' or '\uFEFF';
    }
}
