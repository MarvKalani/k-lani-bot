using System.Globalization;
using System.Text;
using KLaniBot.ControllerTray;
using KLaniBot.Localization;

namespace KLaniBot.Fleet;

// One node's history as the report draws it.
internal sealed record NodeHistory(string Node, IReadOnlyList<VitalsSample> Samples);

// One drawn point: the worst value of its stretch of time, and whether a limit for that value was crossed there.
internal sealed record ChartPoint(DateTimeOffset At, double Value, bool Alert);

// Use case: the operator opens, from the controller tray, how every node was doing over the kept history and sees at a
// glance when load shot up or memory or disk ran short. The page is one private file without scripts or network access:
// inline SVG charts per node for load, free memory and free disk, red dots where a limit was crossed, and the period's
// peaks taken from every sample. Long histories are thinned to a bounded number of points per chart, each the worst value
// of its stretch, so a spike stays visible. Everything a node reported is HTML-escaped.
internal static class NodeVitalsReport
{
    public const string FileName = "vitals-report.html";
    public const int MaximumPoints = 300;
    private const double Gigabyte = 1024.0 * 1024 * 1024;
    private const int Width = 900;
    private const int Height = 170;
    private const int Left = 56;
    private const int Bottom = 24;
    private const int Top = 8;

    public static string Build(IReadOnlyList<NodeHistory> nodes, TimeSpan period, DateTimeOffset now)
    {
        DateTimeOffset from = now - period;
        var page = new StringBuilder();
        page.Append("<!doctype html>\n<html><head><meta charset=\"utf-8\">")
            .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>").Append(Text(UiTexts.Get(UiText.VitalsReportTitle))).Append("</title>")
            .Append("<style>").Append(Style).Append("</style></head><body><main>")
            .Append("<h1>").Append(Text(UiTexts.Get(UiText.VitalsReportTitle))).Append("</h1>")
            .Append("<p class=\"meta\">")
            .Append(Text(UiTexts.Format(UiText.VitalsReportPeriod, Time(from), Time(now), Time(now))))
            .Append("</p><p class=\"meta\">").Append(Text(UiTexts.Get(UiText.VitalsReportLegend))).Append("</p>");
        foreach (NodeHistory node in nodes)
        {
            AppendNode(page, node, from, now);
        }
        return page.Append("</main></body></html>\n").ToString();
    }

    private const string Style =
        ":root{--bg:#f7f8fa;--fg:#17202a;--muted:#5b6672;--grid:#d9dee4;--line:#2563eb;--alert:#dc2626;--card:#fff}"
        + "@media (prefers-color-scheme: dark){:root{--bg:#10161d;--fg:#e6ebf0;--muted:#97a3af;--grid:#2a3440;"
        + "--line:#60a5fa;--alert:#f87171;--card:#17202a}}"
        + "body{margin:0;background:var(--bg);color:var(--fg);font:15px/1.45 system-ui,sans-serif}"
        + "main{max-width:960px;margin:0 auto;padding:16px}section{background:var(--card);border-radius:8px;padding:12px 16px;"
        + "margin:16px 0}h2{margin:4px 0}.meta,.peaks{color:var(--muted)}h3{font-size:14px;margin:12px 0 2px}"
        + "svg{width:100%;height:auto}.grid{stroke:var(--grid)}.axis{fill:var(--muted);font-size:11px}"
        + "polyline{fill:none;stroke:var(--line);stroke-width:1.5}.alert{fill:var(--alert)}";

    private static void AppendNode(StringBuilder page, NodeHistory node, DateTimeOffset from, DateTimeOffset now)
    {
        page.Append("<section><h2>").Append(Text(node.Node)).Append("</h2>");
        if (node.Samples.Count == 0)
        {
            page.Append("<p class=\"meta\">").Append(Text(UiTexts.Get(UiText.VitalsReportNoSamples))).Append("</p></section>");
            return;
        }
        NodeVitals latest = node.Samples[^1].Vitals;
        page.Append("<p class=\"meta\">").Append(Text(latest.System)).Append("</p>")
            .Append("<p class=\"peaks\">").Append(Text(Peaks(node.Samples))).Append("</p>");
        AppendChart(page, UiTexts.Get(UiText.VitalsReportCpu), 100, from, now,
            Thin(node.Samples, sample => sample.Vitals.CpuLoadPercent, NodeAlerts.CpuHigh, highest: true, from, now));
        double memory = node.Samples.Max(sample => sample.Vitals.MemoryTotalBytes ?? 0) / Gigabyte;
        AppendChart(page, UiTexts.Get(UiText.VitalsReportMemory), memory, from, now,
            Thin(node.Samples, sample => sample.Vitals.MemoryAvailableBytes / Gigabyte, NodeAlerts.MemoryLow, highest: false, from, now));
        double disk = node.Samples.Max(sample => sample.Vitals.DiskTotalBytes ?? 0) / Gigabyte;
        AppendChart(page, UiTexts.Format(UiText.VitalsReportDisk, latest.DiskVolume), disk, from, now,
            Thin(node.Samples, sample => sample.Vitals.DiskFreeBytes / Gigabyte, NodeAlerts.DiskLow, highest: false, from, now));
        page.Append("</section>");
    }

    // The peaks come from every sample, not from the thinned chart line.
    private static string Peaks(IReadOnlyList<VitalsSample> samples)
    {
        var parts = new List<string>();
        VitalsSample? busiest = samples.Where(sample => sample.Vitals.CpuLoadPercent != null)
            .MaxBy(sample => sample.Vitals.CpuLoadPercent);
        if (busiest != null)
        {
            string load = Math.Round(busiest.Vitals.CpuLoadPercent!.Value).ToString(CultureInfo.InvariantCulture);
            parts.Add(UiTexts.Format(UiText.VitalsReportPeakCpu, load, Time(busiest.At)));
        }
        VitalsSample? tightest = samples.Where(sample => sample.Vitals.MemoryAvailableBytes != null)
            .MinBy(sample => sample.Vitals.MemoryAvailableBytes);
        if (tightest != null)
        {
            string free = NodeVitalsText.Gigabytes(tightest.Vitals.MemoryAvailableBytes!.Value);
            parts.Add(UiTexts.Format(UiText.VitalsReportPeakMemory, free, Time(tightest.At)));
        }
        VitalsSample? fullest = samples.Where(sample => sample.Vitals.DiskFreeBytes != null).MinBy(sample => sample.Vitals.DiskFreeBytes);
        if (fullest != null)
        {
            parts.Add(UiTexts.Format(UiText.VitalsReportPeakDisk, NodeVitalsText.Gigabytes(fullest.Vitals.DiskFreeBytes!.Value),
                Time(fullest.At)));
        }
        int crossed = samples.Count(sample => sample.Alerts != NodeAlerts.None);
        if (crossed > 0)
        {
            parts.Add(UiTexts.Format(UiText.VitalsReportAlerts, crossed));
        }
        return string.Join(" · ", parts);
    }

    // Splits the period into MaximumPoints equal stretches and keeps the worst value of each.
    private static List<ChartPoint> Thin(IReadOnlyList<VitalsSample> samples, Func<VitalsSample, double?> value, NodeAlerts alert,
        bool highest, DateTimeOffset from, DateTimeOffset now)
    {
        var stretches = new SortedDictionary<int, ChartPoint>();
        double span = Math.Max(1, (now - from).TotalSeconds);
        foreach (VitalsSample sample in samples)
        {
            if (value(sample) is not { } current)
            {
                continue;
            }
            int stretch = (int)Math.Clamp((sample.At - from).TotalSeconds / span * MaximumPoints, 0, MaximumPoints - 1);
            bool crossed = sample.Alerts.HasFlag(alert);
            if (!stretches.TryGetValue(stretch, out ChartPoint? kept))
            {
                stretches[stretch] = new ChartPoint(sample.At, current, crossed);
                continue;
            }
            bool worse = highest ? current > kept.Value : current < kept.Value;
            stretches[stretch] = worse
                ? new ChartPoint(sample.At, current, crossed || kept.Alert)
                : kept with
                {
                    Alert = kept.Alert || crossed
                };
        }
        return stretches.Values.ToList();
    }

    private static void AppendChart(StringBuilder page, string title, double maximum, DateTimeOffset from, DateTimeOffset now,
        List<ChartPoint> points)
    {
        page.Append("<h3>").Append(Text(title)).Append("</h3>");
        double top = maximum > 0 ? maximum : Math.Max(1, points.Select(point => point.Value).DefaultIfEmpty(1).Max());
        double span = Math.Max(1, (now - from).TotalSeconds);
        string X(DateTimeOffset at) => Number(Left + (Width - Left - 8) * Math.Clamp((at - from).TotalSeconds / span, 0, 1));
        string Y(double value) => Number(Top + (Height - Top - Bottom) * (1 - Math.Clamp(value / top, 0, 1)));

        page.Append("<svg viewBox=\"0 0 ").Append(Width).Append(' ').Append(Height).Append("\" role=\"img\" aria-label=\"")
            .Append(Text(title)).Append("\">");
        foreach (double share in new[] { 0.0, 0.5, 1.0 })
        {
            string y = Y(top * share);
            page.Append("<line class=\"grid\" x1=\"").Append(Left).Append("\" x2=\"").Append(Width - 8).Append("\" y1=\"").Append(y)
                .Append("\" y2=\"").Append(y).Append("\"/><text class=\"axis\" x=\"4\" y=\"").Append(y).Append("\">")
                .Append(Text(Axis(top * share))).Append("</text>");
        }
        page.Append("<text class=\"axis\" x=\"").Append(Left).Append("\" y=\"").Append(Height - 6).Append("\">")
            .Append(Text(Time(from))).Append("</text><text class=\"axis\" text-anchor=\"end\" x=\"").Append(Width - 8)
            .Append("\" y=\"").Append(Height - 6).Append("\">").Append(Text(Time(now))).Append("</text>");
        page.Append("<polyline points=\"").Append(string.Join(' ', points.Select(point => X(point.At) + "," + Y(point.Value))))
            .Append("\"/>");
        foreach (ChartPoint point in points.Where(point => point.Alert))
        {
            page.Append("<circle class=\"alert\" r=\"3\" cx=\"").Append(X(point.At)).Append("\" cy=\"").Append(Y(point.Value))
                .Append("\"/>");
        }
        page.Append("</svg>");
    }

    private static string Axis(double value)
    {
        string text = value.ToString(value < 10 ? "0.#" : "0", CultureInfo.InvariantCulture);
        return UiTexts.Language == UiLanguage.German ? text.Replace('.', ',') : text;
    }

    private static string Number(double value)
    {
        return value.ToString("0.#", CultureInfo.InvariantCulture);
    }

    // The operator reads the report on the master's computer, so its own clock is the one that makes sense.
    private static string Time(DateTimeOffset at)
    {
        return at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    // Only the characters that carry meaning in HTML are replaced; umlauts stay readable in the UTF-8 page.
    private static string Text(string value)
    {
        return value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&#39;", StringComparison.Ordinal);
    }
}
