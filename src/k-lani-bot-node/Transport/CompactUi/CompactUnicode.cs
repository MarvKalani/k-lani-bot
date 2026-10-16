using System.Text;

namespace KLaniBot.Transport.CompactUi;

// Compact paging and clipping use Unicode codepoints, matching the JavaScript reference's Array.from semantics.
public static class CompactUnicode
{
    public static int Count(string text)
    {
        int count = 0;
        foreach (Rune character in text.EnumerateRunes())
        {
            count++;
        }

        return count;
    }

    public static string Slice(string text, int offset, int limit)
    {
        var output = new StringBuilder();
        int position = 0;
        foreach (Rune character in text.EnumerateRunes())
        {
            if (position >= offset && position - offset < limit)
            {
                output.Append(character.ToString());
            }
            position++;
            if (position >= offset && position - offset >= limit)
            {
                break;
            }
        }

        return output.ToString();
    }
}
