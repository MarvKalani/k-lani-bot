namespace KLaniBot.Platforms.MacOS;

// The macOS tree reader calls this for each accessibility element before it reads that element's value. Secure text fields
// share the text field role, so only their subrole tells a password apart, and a password value is never read.
public static class MacAccessibilityRoleName
{
    public const string SecureTextField = "SecureTextField";
    private const string AccessibilityPrefix = "AX";
    private const string TextFieldRole = "AXTextField";
    private const string SecureTextFieldSubrole = "AXSecureTextField";

    // Only a text field can be a secure text field, so no other role needs its subrole read.
    public static bool ReadsSubrole(string role)
    {
        return role == TextFieldRole;
    }

    // A missing or other subrole is an ordinary text field whose value stays visible.
    public static bool HidesValue(string role, string? subrole)
    {
        return role == TextFieldRole && subrole == SecureTextFieldSubrole;
    }

    // Secure fields get their own name; every other role keeps its name without the accessibility prefix.
    public static string From(string role, string? subrole)
    {
        if (HidesValue(role, subrole))
        {
            return SecureTextField;
        }

        return role.StartsWith(AccessibilityPrefix, StringComparison.Ordinal) ? role[AccessibilityPrefix.Length..] : role;
    }
}
