using System.Text.RegularExpressions;

namespace KLaniBot.Inventory;

// Use case: a Windows machine lists hundreds of entries that no person operates: runtimes, redistributables,
// drivers, update packages. An administrator reading the scan wants those apart from the programs. This is a
// heuristic on the display name, so it only sorts the report; nothing is hidden and nothing is decided with it.
public static class ApplicationKindClassifier
{
    private static readonly Regex ComponentName = new(
        @"(redistributable|runtime|\bdriver\b|\bupdate for\b|security update|hotfix|\bKB\d{5,}\b|service pack|"
        + @"\.NET (Core|Framework|Runtime|SDK|Desktop)|windows sdk|visual c\+\+ \d|vcredist|language pack|"
        + @"\bcodec pack\b|webview2)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static ApplicationKind Classify(string name)
    {
        return ComponentName.IsMatch(name) ? ApplicationKind.Component : ApplicationKind.Application;
    }
}
