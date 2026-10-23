namespace RepositoryGuard;

// Publication checks reject real contact addresses in example configuration files. RFC 2606 (June 1999) reserved
// documentation domains remain usable, while findings reveal only the file and line, never the address.
internal static class ExamplePrivacyPolicy
{
    private const int MaximumFindings = 100;
    internal static readonly System.Text.RegularExpressions.Regex EmailPattern = new(
        @"[A-Za-z0-9._%+\-]+@(?<domain>[A-Za-z0-9.\-]+\.[A-Za-z]{2,})",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public static IReadOnlyList<Finding> Inspect(string path, string content, string? commit)
    {
        if (!Path.GetFileName(path).Contains(".example.", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var findings = new List<Finding>();
        foreach (System.Text.RegularExpressions.Match match in EmailPattern.Matches(content))
        {
            if (!IsDocumentationDomain(match.Groups["domain"].Value))
            {
                int line = content.AsSpan(0, match.Index).Count('\n') + 1;
                findings.Add(new("personal-address-in-example", path, line, commit));
                if (findings.Count == MaximumFindings)
                {
                    break;
                }
            }
        }

        return findings;
    }

    internal static bool IsDocumentationDomain(string domain)
    {
        string normalized = domain.ToLowerInvariant();
        string[] reservedDomains = ["example.com", "example.org", "example.net", "example", "invalid", "test", "localhost"];

        return reservedDomains.Any(reserved => normalized == reserved || normalized.EndsWith("." + reserved, StringComparison.Ordinal));
    }
}
