using KLaniBot.Platforms.Linux;

// The Linux tree reader and the text writes refuse an AT-SPI password field by its role name alone. The decision is pure, so
// it is checked on every platform; the AT-SPI calls that apply it need a Linux desktop and are not run here.
internal static class LinuxPasswordRoleCases
{
    public static int Run()
    {
        var checks = new Checks("Linux password role");
        CheckPasswordRole(checks);
        CheckOrdinaryRoles(checks);
        CheckRefusal(checks);

        return checks.Passed;
    }

    private static void CheckPasswordRole(Checks checks)
    {
        checks.That(LinuxPasswordRole.IsPasswordRole("password text"), "a password text role is a password field");
        checks.That(LinuxPasswordRole.IsPasswordRole("Password Text"), "the password text role matches in any letter case");
    }

    private static void CheckOrdinaryRoles(Checks checks)
    {
        checks.That(!LinuxPasswordRole.IsPasswordRole("text"), "a text role is an ordinary field");
        checks.That(!LinuxPasswordRole.IsPasswordRole("entry"), "an entry role is an ordinary field");
        checks.That(!LinuxPasswordRole.IsPasswordRole("password"), "a bare password role is an ordinary element");
        checks.That(!LinuxPasswordRole.IsPasswordRole(""), "an empty role is an ordinary element");
        checks.That(!LinuxPasswordRole.IsPasswordRole(null), "a missing role is an ordinary element");
        checks.That(!LinuxPasswordRole.IsPasswordRole("password text extra"), "a longer role name is not a password field");
    }

    private static void CheckRefusal(Checks checks)
    {
        checks.That(RefusesWithMessage("password text"), "a password field refuses text access with the refusal message");
        checks.That(!RefusesWithMessage("text"), "an ordinary text field is not refused");
    }

    // Reports whether the guard throws the refusal exception for this role, so a missing guard fails the check.
    private static bool RefusesWithMessage(string role)
    {
        try
        {
            LinuxPasswordRole.ThrowIfPassword(role);
            return false;
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message == LinuxPasswordRole.RefusalMessage;
        }
    }
}
