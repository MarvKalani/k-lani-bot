using System.Text.RegularExpressions;

namespace KLaniBot.Guard;

// Use case: the operator allows a program for launch_process and may also say with which arguments, because an
// allowed powershell.exe with any argument would allow any command. Each allowed form is a list of patterns, one per
// argument, matched whole and position by position; a program without forms keeps accepting any arguments.
public static class ArgumentPatterns
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    // Throws InvalidDataException when a program with forms is not in the principal's applications or a pattern is invalid.
    public static void Validate(PrincipalPolicy principal)
    {
        if (principal.ApplicationArguments is null)
        {
            throw new InvalidDataException("Application arguments must not be null; omit them to allow any arguments.");
        }
        foreach (var (program, forms) in principal.ApplicationArguments)
        {
            bool listed = principal.Applications.Any(application => SameProgram(application, program));
            if (!listed || forms is null || forms.Any(form => form is null || form.Any(pattern => !IsValidPattern(pattern))))
            {
                throw new InvalidDataException("Argument forms need a listed application and valid patterns.");
            }
        }
    }

    // Null when the operator gave no forms for this program, so any arguments are allowed.
    public static string[][]? FormsFor(PrincipalPolicy principal, string program)
    {
        foreach (var (listed, forms) in principal.ApplicationArguments)
        {
            if (SameProgram(listed, program))
            {
                return forms;
            }
        }
        return null;
    }

    // A pattern that takes too long to match counts as not matching.
    public static bool Matches(string[] form, IReadOnlyList<string> arguments)
    {
        if (form.Length != arguments.Count)
        {
            return false;
        }

        try
        {
            return form.Zip(arguments).All(pair => Regex.IsMatch(pair.Second, Anchored(pair.First), RegexOptions.CultureInvariant,
                MatchTimeout));
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    // Paths are compared as full paths, Android package names as they are.
    private static bool SameProgram(string left, string right)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(Normalized(left), Normalized(right), comparison);
    }

    private static string Normalized(string program)
    {
        return Path.IsPathFullyQualified(program) ? Path.GetFullPath(program) : program;
    }

    // The whole argument must match, so a pattern for "--export" does not also allow "--export; rm -rf".
    private static string Anchored(string pattern)
    {
        return @"\A(?:" + pattern + @")\z";
    }

    private static bool IsValidPattern(string? pattern)
    {
        if (pattern is null)
        {
            return false;
        }

        try
        {
            _ = new Regex(Anchored(pattern), RegexOptions.CultureInvariant, MatchTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
