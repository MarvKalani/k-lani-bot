using KLaniBot.Platforms.MacOS;

// The macOS tree reader hides a password value by its subrole alone and names each element from its role. The decision is
// pure, so it is checked on every platform; the accessibility reads themselves need a Mac and are covered by the Mac acceptance.
internal static class MacAccessibilityRoleNameCases
{
    public static int Run()
    {
        var checks = new Checks("Mac accessibility role name");
        CheckSecureField(checks);
        CheckOrdinaryTextField(checks);
        CheckOtherRoles(checks);

        return checks.Passed;
    }

    private static void CheckSecureField(Checks checks)
    {
        checks.That(MacAccessibilityRoleName.ReadsSubrole("AXTextField"), "a text field has its subrole read");
        checks.That(MacAccessibilityRoleName.HidesValue("AXTextField", "AXSecureTextField"), "a secure text field hides its value");
        checks.That(MacAccessibilityRoleName.From("AXTextField", "AXSecureTextField") == "SecureTextField",
            "a secure text field is named SecureTextField");
    }

    private static void CheckOrdinaryTextField(Checks checks)
    {
        checks.That(!MacAccessibilityRoleName.HidesValue("AXTextField", null), "a text field without subrole shows its value");
        checks.That(!MacAccessibilityRoleName.HidesValue("AXTextField", ""), "a text field with an empty subrole shows its value");
        checks.That(!MacAccessibilityRoleName.HidesValue("AXTextField", "AXSearchField"), "a search field shows its value");
        checks.That(MacAccessibilityRoleName.From("AXTextField", null) == "TextField", "a text field keeps the TextField name");
        checks.That(MacAccessibilityRoleName.From("AXTextField", "AXSearchField") == "TextField", "a search field is named TextField");
    }

    private static void CheckOtherRoles(Checks checks)
    {
        checks.That(MacAccessibilityRoleName.From("AXButton", null) == "Button", "an accessibility prefix is removed from a button");
        checks.That(MacAccessibilityRoleName.From("AXPopUpButton", null) == "PopUpButton", "a pop-up button keeps its name");
        checks.That(!MacAccessibilityRoleName.ReadsSubrole("AXButton"), "a button has no subrole read");
        checks.That(MacAccessibilityRoleName.From("StaticText", null) == "StaticText", "a role without prefix passes through");
        checks.That(!MacAccessibilityRoleName.HidesValue("AXButton", "AXSecureTextField"), "only a text field can hide its value");
    }
}
