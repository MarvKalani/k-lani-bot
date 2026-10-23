using System.Text.RegularExpressions;

namespace RepositoryGuard;

// The repository is published: no address but the project's public contact may appear in files, commit metadata or
// messages, and no name of the owner's own machines or network. Those names would make the published guard a map of the
// lab, so they live in a term list inside the Git directory, which Git never commits or pushes. Findings name only the
// file and line, never the address or term.
public static class PublicationIdentityPolicy
{
    public const string TermFileName = "repository-guard-private-terms";
    private const int MaximumFindings = 100;
    // noreply@anthropic.com is the Co-Authored-By trailer already in the published history; the pre-push scan reads the
    // full ancestry, so rejecting it would block every push.
    private static readonly string[] ApprovedAddresses = ["support@kalanis.de", "noreply@anthropic.com"];

    // One term per line; blank lines and lines starting with '#' are ignored. A missing file means no private terms.
    public static IReadOnlyList<string> ReadTerms(string gitCommonDirectory)
    {
        string file = Path.Combine(gitCommonDirectory, "info", TermFileName);
        if (!File.Exists(file))
        {
            return [];
        }

        return File.ReadAllLines(file)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
    }

    public static bool PathHasTerm(string path, IReadOnlyList<string> terms) => terms.Any(term => TermPattern(term).IsMatch(path));

    public static IReadOnlyList<Finding> Inspect(string path, string content, string? commit, IReadOnlyList<string> terms)
    {
        var findings = new List<Finding>();
        foreach (Match match in ExamplePrivacyPolicy.EmailPattern.Matches(content))
        {
            if (!ApprovedAddresses.Contains(match.Value, StringComparer.OrdinalIgnoreCase)
                && !ExamplePrivacyPolicy.IsDocumentationDomain(match.Groups["domain"].Value))
            {
                findings.Add(new("unapproved-email-address", path, LineOf(content, match.Index), commit));
            }
        }
        foreach (string term in terms)
        {
            foreach (Match match in TermPattern(term).Matches(content))
            {
                findings.Add(new("private-machine-or-network-name", path, LineOf(content, match.Index), commit));
            }
        }

        return findings.Take(MaximumFindings).ToArray();
    }

    // A term matches as a whole word, so a private host name does not reject every longer identifier that contains it.
    private static Regex TermPattern(string term)
        => new(
            @"(?<![A-Za-z0-9])" + Regex.Escape(term) + @"(?![A-Za-z0-9])",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

    private static int LineOf(string content, int index) => content.AsSpan(0, index).Count('\n') + 1;
}
