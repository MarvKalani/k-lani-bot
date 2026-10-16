namespace KLaniBot.Platforms.Linux;

// The AT-SPI tree reader asks this before it reads an element's text, and every text write asks ThrowIfPassword before it
// dispatches anything. AT-SPI names a password entry "password text"; its text is neither read nor written, so a refused
// write sends no input at all.
public static class LinuxPasswordRole
{
    public const string PasswordTextRole = "password text";
    public const string RefusalMessage = "Password fields are not read or written; no input was sent.";

    // The exact AT-SPI name in any letter case; a missing name is an ordinary element.
    public static bool IsPasswordRole(string? role)
    {
        return string.Equals(role, PasswordTextRole, StringComparison.OrdinalIgnoreCase);
    }

    // Throws InvalidOperationException for a password field, so the caller stops before any input is dispatched.
    public static void ThrowIfPassword(string? role)
    {
        if (IsPasswordRole(role))
        {
            throw new InvalidOperationException(RefusalMessage);
        }
    }
}
