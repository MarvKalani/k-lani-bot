using System.Text;
using System.Text.RegularExpressions;

namespace KLaniBot.Startup;

// What may leave the process about an exception that was caught: its type and a short message.
// Framework messages carry file paths of the operator's machine and occasionally header or token
// values. Logs and error frames are kept and passed around, so both are removed here. Paths below
// the node's own state or workspace stay readable because they are what an operator has to inspect.
public static partial class Diagnostics
{
    private const int MessageLimit = 240;
    private const string RedactedSecret = "<redacted>";
    private const string RedactedPath = "<path>";

    public static string Describe(Exception exception, params string[] readableRoots)
    {
        return exception.GetType().Name + ": " + Clean(exception.Message, readableRoots);
    }

    // Paths are judged first and as a whole: a readable path keeps its long directory names, which
    // the secret rule would otherwise take for tokens.
    public static string Clean(string message, params string[] readableRoots)
    {
        string withoutBearer = BearerValue().Replace(message, "Bearer " + RedactedSecret);
        var cleaned = new StringBuilder();
        int position = 0;
        foreach (Match path in AbsolutePath().Matches(withoutBearer))
        {
            cleaned.Append(RedactSecrets(withoutBearer[position..path.Index]));
            cleaned.Append(RedactUnlessReadable(path, readableRoots));
            position = path.Index + path.Length;
        }
        cleaned.Append(RedactSecrets(withoutBearer[position..]));
        string singleLine = cleaned.ToString().ReplaceLineEndings(" ");
        return Truncate(singleLine);
    }

    private static string RedactSecrets(string text)
    {
        return SecretLikeRun().Replace(text, RedactUnlessIdentifier);
    }

    // Request and operation ids are UUIDs and are exactly what an operator searches the journal for.
    private static string RedactUnlessIdentifier(Match match)
    {
        if (Guid.TryParse(match.Value, out _))
        {
            return match.Value;
        }
        return RedactedSecret;
    }

    private static string RedactUnlessReadable(Match match, string[] readableRoots)
    {
        string path = match.Value.TrimEnd('.', ',', ';', ')');
        string trailing = match.Value[path.Length..];
        foreach (string root in readableRoots)
        {
            if (IsBelow(path, root))
            {
                return match.Value;
            }
        }
        return RedactedPath + trailing;
    }

    private static bool IsBelow(string path, string root)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (path.Equals(fullRoot, StringComparison.Ordinal))
        {
            return true;
        }
        return path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string Truncate(string message)
    {
        if (message.Length <= MessageLimit)
        {
            return message;
        }
        var shortened = new StringBuilder(message, 0, MessageLimit, MessageLimit + 1);
        shortened.Append('…');
        return shortened.ToString();
    }

    [GeneratedRegex(@"Bearer\s+\S+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerValue();

    // Node credentials are 43 base64url characters, pairing secrets and keys are longer. Any unbroken
    // run of that alphabet from 32 characters on is treated as a possible secret.
    [GeneratedRegex(@"[A-Za-z0-9_\-+/=]{32,}")]
    private static partial Regex SecretLikeRun();

    // A slash after a word character or a colon belongs to base64 or a URL, not to a file path.
    [GeneratedRegex(@"(?<![\w.~:/])(?:/[^\s'""/<>]+)+/?|\b[A-Za-z]:\\[^\s'""<>]*")]
    private static partial Regex AbsolutePath();
}
