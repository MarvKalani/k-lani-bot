using System.Text.RegularExpressions;

namespace KLaniBot.Platforms;

// Sorting reads retain all prose while replacing verbose link targets with their host and path. A caller can request
// Raw text by the same item identity when a query parameter is relevant; the content hash always binds the original.
internal static class ApplicationMailText
{
    private const int MaximumLinkPathCharacters = 80;
    private static readonly Regex LinkedTarget = new("<https?://[^<>\\s]+>", RegexOptions.NonBacktracking);
    private static readonly Regex RepeatedSpaces = new("[ \\t]{2,}", RegexOptions.NonBacktracking);
    private static readonly Regex OriginalLinks = new(@"<(?:https?|mailto|data):[^>]*>|\[link: [^\]]*\]",
        RegexOptions.NonBacktracking);
    private static readonly Regex BareLinks = new(@"https?://[^\s<>]+", RegexOptions.NonBacktracking);
    private static readonly Regex InvisiblePadding = new(@"[\p{Cf}\u034f\u2800]", RegexOptions.NonBacktracking);
    private static readonly Regex BlankLines = new(@"\n\s*\n(?:\s*\n)*", RegexOptions.NonBacktracking);

    public static string Compact(string body)
    {
        string text = body.Replace("\u034f", "").Replace("\u200b", "").Replace("\ufeff", "");
        text = LinkedTarget.Replace(text, LinkSummary);

        return RepeatedSpaces.Replace(text, " ");
    }

    public static string Readable(string body)
    {
        string text = body.ReplaceLineEndings("\n");
        text = OriginalLinks.Replace(text, " ");
        text = BareLinks.Replace(text, "[link]");
        text = InvisiblePadding.Replace(text, "");
        text = RepeatedSpaces.Replace(text, " ");
        text = BlankLines.Replace(text, "\n\n");

        return text.Trim();
    }

    private static string LinkSummary(Match match)
    {
        if (!Uri.TryCreate(match.Value[1..^1], UriKind.Absolute, out Uri? target))
        {
            return "[link target omitted; request Raw text]";
        }
        string path = target.AbsolutePath;
        if (path.Length > MaximumLinkPathCharacters)
        {
            path = path[..MaximumLinkPathCharacters] + "…";
        }

        return "[link: " + target.Host + path + "]";
    }
}
