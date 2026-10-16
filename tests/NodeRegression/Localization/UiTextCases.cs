using System.Text.Json;
using System.Text.RegularExpressions;
using KLaniBot.Localization;
using KLaniBot.Visibility;

// A text missing from one language would throw in the middle of drawing a menu, and a placeholder missing from one
// translation would drop the master's name or the task from what the person reads.
internal static class UiTextCases
{
    public static int Run()
    {
        var checks = new Checks("UI text");

        CheckTables(checks);
        CheckLanguageSwitch(checks);
        checks.That(JsonSerializer.Deserialize<UiLanguage>("\"de\"") == UiLanguage.German, "the setting is read as its word");
        return checks.Passed;
    }

    private static void CheckTables(Checks checks)
    {
        foreach (UiLanguage language in Enum.GetValues<UiLanguage>())
        {
            IReadOnlyDictionary<UiText, string> table = UiTexts.TableOf(language);
            foreach (UiText text in Enum.GetValues<UiText>())
            {
                checks.That(table.TryGetValue(text, out string? words) && words.Trim().Length > 0, language + " has " + text);
            }
        }
        foreach (UiText text in Enum.GetValues<UiText>())
        {
            string[] english = Placeholders(UiTexts.TableOf(UiLanguage.English)[text]);
            string[] german = Placeholders(UiTexts.TableOf(UiLanguage.German)[text]);
            checks.That(english.SequenceEqual(german), "both languages fill the same placeholders in " + text);
        }
    }

    // The language is process-wide, so it is restored even when a check fails.
    private static void CheckLanguageSwitch(Checks checks)
    {
        UiLanguage before = UiTexts.Language;
        try
        {
            UiTexts.Use(UiLanguage.German);
            var unstated = SessionPresentation.Create(null, null, rights: "observe, teleport");
            checks.That(unstated.ShownClient == "Nicht benannter Client", "an unstated client is shown in the chosen language");
            bool rightsDescribed = unstated.DescribeRights() == "Bildschirm und Bedienelemente lesen; teleport";
            checks.That(rightsDescribed, "a known right is described, an unknown one named");
            UiTexts.Use(UiLanguage.English);
            checks.That(unstated.ShownClient == "Unnamed client", "the same presentation switches language");
            checks.That(unstated.Client == "", "the stored value stays empty instead of a translated placeholder");
        }
        finally
        {
            UiTexts.Use(before);
        }
    }

    private static string[] Placeholders(string text)
    {
        return Regex.Matches(text, @"\{\d+\}").Select(match => match.Value).Order().ToArray();
    }
}
