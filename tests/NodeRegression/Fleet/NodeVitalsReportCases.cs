using System.Text.RegularExpressions;
using KLaniBot.ControllerTray;
using KLaniBot.Fleet;
using KLaniBot.Localization;

// The vitals report the operator opens from the tray: one self-contained page without scripts, one section per node with
// charts for load, free memory and free disk, crossed limits marked, long histories thinned without losing their worst
// values, and node text escaped.
internal static class NodeVitalsReportCases
{
    private const long Gigabyte = 1024L * 1024 * 1024;
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static int Run()
    {
        var checks = new Checks("Node vitals report");
        UiLanguage previous = UiTexts.Language;
        UiTexts.Use(UiLanguage.German);
        try
        {
            ReportShowsEveryNode(checks);
            LongHistoryIsThinnedKeepingTheWorst(checks);
            NodeTextIsEscaped(checks);
            TrayOffersTheReport(checks);
        }
        finally
        {
            UiTexts.Use(previous);
        }
        return checks.Passed;
    }

    private static NodeVitals Vitals(double cpu, long memory, long disk)
    {
        return new NodeVitals("Ubuntu 26.04 LTS", memory, 16 * Gigabyte, "/", disk, 500 * Gigabyte, cpu, 3600);
    }

    private static List<VitalsSample> Minutes(int count, Func<int, VitalsSample> sample)
    {
        return Enumerable.Range(0, count).Select(sample).ToList();
    }

    private static void ReportShowsEveryNode(Checks checks)
    {
        List<VitalsSample> office = Minutes(60, minute => new VitalsSample(Start.AddMinutes(minute),
            Vitals(minute == 30 ? 97 : 10, 8 * Gigabyte, 200 * Gigabyte), minute == 30 ? NodeAlerts.CpuHigh : NodeAlerts.None));
        string html = NodeVitalsReport.Build([new("office-pc", office), new("laptop", [])], TimeSpan.FromDays(1), Start.AddHours(1));

        checks.That(html.StartsWith("<!doctype html>", StringComparison.Ordinal) && html.Contains("<h2>office-pc</h2>")
            && html.Contains("<h2>laptop</h2>"), "every node gets its own section");
        checks.That(Regex.Count(html, "<svg ") == 3, "a node with samples gets charts for load, free memory and free disk");
        checks.That(html.Contains("Keine Werte in diesem Zeitraum"), "a node without samples says so instead of drawing empty charts");
        checks.That(html.Contains("höchste CPU-Last 97 %") && html.Contains("class=\"alert\""),
            "the peak is named and the crossed limit is marked in the chart");
        checks.That(!html.Contains("<script", StringComparison.OrdinalIgnoreCase)
            && html.Contains("Content-Security-Policy") && !html.Contains("http://") && !html.Contains("https://"),
            "the page runs no script and loads nothing from the network");
    }

    private static void LongHistoryIsThinnedKeepingTheWorst(Checks checks)
    {
        List<VitalsSample> week = Minutes(7 * 24 * 60, minute => new VitalsSample(Start.AddMinutes(minute),
            Vitals(minute == 5000 ? 99 : 20, minute == 7000 ? Gigabyte / 5 : 8 * Gigabyte, 200 * Gigabyte), NodeAlerts.None));
        string html = NodeVitalsReport.Build([new("office-pc", week)], TimeSpan.FromDays(7), Start.AddDays(7));
        int largestLine = Regex.Matches(html, "points=\"([^\"]*)\"").Max(match => match.Groups[1].Value.Split(' ').Length);

        checks.That(largestLine <= NodeVitalsReport.MaximumPoints, "a week of minutes is drawn with a bounded number of points");
        checks.That(html.Contains("höchste CPU-Last 99 %") && html.Contains("wenigster freier RAM 0,2 GB"),
            "the peaks are taken from every sample, not from the thinned line");
        checks.That(html.Length < 400_000, "the page stays small enough to open at once");
    }

    private static void NodeTextIsEscaped(Checks checks)
    {
        var sample = new VitalsSample(Start, Vitals(10, Gigabyte, Gigabyte) with
        {
            System = "<img src=x onerror=alert(1)>"
        },
            NodeAlerts.None);
        string html = NodeVitalsReport.Build([new("office-pc", [sample])], TimeSpan.FromDays(1), Start.AddHours(1));

        checks.That(!html.Contains("<img") && html.Contains("&lt;img"), "text a node reported is shown, never interpreted");
    }

    private static void TrayOffersTheReport(Checks checks)
    {
        var status = new ControllerStatus(BotSwitch.On, [new ControllerNodeRow("office-pc", true, "")], 0, [], 0);
        ControllerTrayView view = ControllerTrayMenu.Build(status, [], "");
        ControllerTrayEntry? open = view.Entries.SingleOrDefault(entry => entry.Command?.Action == ControllerTrayAction.OpenVitalsReport);

        checks.That(open?.Text == "Verlauf der Vitalwerte öffnen" && open.Command!.MasterCommand() == "vitals-report",
            "the tray offers the report and asks the master to write it");
        ControllerTrayView withoutMaster = ControllerTrayMenu.Build(null, ["office-pc"], "");
        checks.That(withoutMaster.Entries.All(entry => entry.Command?.Action != ControllerTrayAction.OpenVitalsReport),
            "without a master there is no report to open");
    }
}
