using System.Text.Json.Nodes;
using KLaniBot.ControllerTray;
using KLaniBot.Fleet;
using KLaniBot.Localization;

// Node vital signs without a second computer: the Linux sources are parsed from fixed text, CPU load is the share of busy
// ticks between two samples, values a node sends are bounded before the tray or a model sees them, and the tray says them
// in the person's language.
internal static class NodeVitalsCases
{
    private const long Gigabyte = 1024L * 1024 * 1024;

    public static int Run(string temp)
    {
        var checks = new Checks("Node vital signs");
        UiLanguage previous = UiTexts.Language;
        try
        {
            LinuxSourcesAreParsed(checks);
            CpuLoadIsTheBusyShareBetweenSamples(checks);
            NodeValuesAreBounded(checks);
            StatusCarriesVitalsAndContact(checks);
            LimitsRaiseAlerts(checks);
            LimitsAreReadFromTheMasterFile(checks);
            HistoryKeepsTheConfiguredRhythmAndPeriod(checks, Path.Combine(temp, "vitals-history-rhythm"));
            HistorySummaryNamesThePeaks(checks, Path.Combine(temp, "vitals-history-peaks"));
            UiTexts.Use(UiLanguage.German);
            TrayShowsVitalsInGerman(checks);
            TrayShowsLastContactOfAnOfflineNode(checks);
            TrayMarksCrossedLimits(checks);
            UiTexts.Use(UiLanguage.English);
            TrayShowsVitalsInEnglish(checks);
        }
        finally
        {
            UiTexts.Use(previous);
        }
        return checks.Passed;
    }

    private static NodeVitals Sample()
    {
        return new NodeVitals("Windows 11 (Build 22621)", 3355443200, 8 * Gigabyte, @"C:\", 41 * Gigabyte, 237 * Gigabyte, 12.4,
            (3 * 24 + 4) * 3600 + 120);
    }

    private static void LinuxSourcesAreParsed(Checks checks)
    {
        var memory = NodeVitalsProbe.LinuxMemory("MemTotal:       16314216 kB\nMemFree:  1000 kB\nMemAvailable:    9876544 kB\n");
        checks.That(memory.Available == 9876544L * 1024 && memory.Total == 16314216L * 1024,
            "free memory is MemAvailable, not MemFree, and kB become bytes");
        checks.That(NodeVitalsProbe.LinuxMemory("MemTotal: 100 kB\n").Available == null, "a missing MemAvailable stays unknown");
        NodeVitalsProbe.CpuTicks? ticks = NodeVitalsProbe.LinuxCpuTicks("cpu  100 5 50 800 40 3 2 0 0 0\ncpu0 1 2 3 4\n");
        checks.That(ticks is { Busy: 160, Total: 1000 }, "idle and iowait count as idle, the other first eight fields as busy");
        checks.That(NodeVitalsProbe.LinuxCpuTicks("intr 1 2 3\n") == null, "a stat file without a cpu line gives no sample");
        string osRelease = "NAME=\"Ubuntu\"\nPRETTY_NAME=\"Ubuntu 26.04 LTS\"\nID=ubuntu\n";
        checks.That(NodeVitalsProbe.LinuxSystemName(osRelease) == "Ubuntu 26.04 LTS", "the distribution's pretty name is used");
        checks.That(NodeVitalsProbe.LinuxSystemName("ID=arch\n") == null, "without a pretty name the caller falls back");
        checks.That(NodeVitalsProbe.LinuxUptimeSeconds("12345.67 99999.00\n") == 12345, "uptime is the first field of /proc/uptime");
        checks.That(NodeVitalsProbe.WindowsSystemName(new Version(10, 0, 22621)) == "Windows 11 (Build 22621)"
            && NodeVitalsProbe.WindowsSystemName(new Version(10, 0, 19045)) == "Windows 10 (Build 19045)",
            "Windows 11 is told apart from Windows 10 by its build number");
    }

    private static void CpuLoadIsTheBusyShareBetweenSamples(Checks checks)
    {
        var before = new NodeVitalsProbe.CpuTicks(100, 1000);
        checks.That(NodeVitalsProbe.LoadPercent(before, new NodeVitalsProbe.CpuTicks(150, 1200)) == 25.0,
            "load is busy ticks over all ticks since the previous sample");
        checks.That(NodeVitalsProbe.LoadPercent(before, before) == null, "no elapsed ticks give no load");
        checks.That(NodeVitalsProbe.LoadPercent(before, new NodeVitalsProbe.CpuTicks(90, 900)) == null,
            "counters that went backwards (a restarted machine) give no load");
        var probe = new NodeVitalsProbe();
        checks.That(probe.Measure().CpuLoadPercent == null, "the first measurement has no load, because it does not wait for a sample");
    }

    private static void NodeValuesAreBounded(Checks checks)
    {
        JsonObject sent = Sample().ToJson();
        sent["system"] = "Evil\u001b[2J " + new string('x', 300);
        sent["memory_available_bytes"] = -5;
        sent["cpu_load_percent"] = 250.0;
        sent["disk_volume"] = "/\nfake";
        NodeVitals? read = NodeVitals.TryRead(sent);
        checks.That(read != null && read.System.Length <= 80 && !read.System.Any(char.IsControl) && !read.DiskVolume.Any(char.IsControl),
            "node text is shortened and loses control characters");
        checks.That(read!.MemoryAvailableBytes == null && read.CpuLoadPercent == null && read.MemoryTotalBytes == 8 * Gigabyte,
            "a number outside its range becomes unknown while the others are kept");
        checks.That(NodeVitals.TryRead(JsonValue.Create("vitals")) == null && NodeVitals.TryRead(null) == null,
            "a reply that is no object carries no vitals");
        checks.That(NodeVitals.TryRead(Sample().ToJson()) == Sample(), "vitals survive the wire unchanged");
    }

    private static void StatusCarriesVitalsAndContact(Checks checks)
    {
        ControllerNodeRow[] nodes =
        [
            new("office-pc", true, "0.2610.0604", Sample(), 4, NodeAlerts.DiskLow | NodeAlerts.CpuHigh),
            new("laptop", false, "", null, null)
        ];
        var status = new ControllerStatus(BotSwitch.On, nodes, 0, [], 0);
        bool read = ControllerStatus.TryRead(status.ToJson(), out ControllerStatus again);
        checks.That(read && again.Nodes[0] == nodes[0] && again.Nodes[1] == nodes[1],
            "controller-status carries each node's vitals, alerts and seconds since last contact, or that they are unknown");
    }

    private static void LimitsRaiseAlerts(Checks checks)
    {
        var limits = new NodeVitalsSettings();
        checks.That(limits.Alerts(Sample()) == NodeAlerts.None, "a node within every limit raises no alert");
        NodeVitals strained = Sample() with
        {
            MemoryAvailableBytes = Gigabyte / 2,
            DiskFreeBytes = 3 * Gigabyte,
            CpuLoadPercent = 95
        };
        checks.That(limits.Alerts(strained) == (NodeAlerts.MemoryLow | NodeAlerts.DiskLow | NodeAlerts.CpuHigh),
            "low memory, a nearly full disk and a busy processor each raise their alert");
        NodeVitals bigDisk = Sample() with
        {
            DiskFreeBytes = 50 * Gigabyte,
            DiskTotalBytes = 2000 * Gigabyte
        };
        checks.That(limits.Alerts(bigDisk) == NodeAlerts.DiskLow, "a large disk below its share of free space is low too");
        NodeVitals unknown = Sample() with
        {
            MemoryAvailableBytes = null,
            DiskFreeBytes = null,
            CpuLoadPercent = null
        };
        checks.That(limits.Alerts(unknown) == NodeAlerts.None, "an unmeasured value raises no alert");
        checks.That(NodeVitalsSettings.Describe(NodeAlerts.DiskLow | NodeAlerts.CpuHigh, strained)
            .SequenceEqual([@"disk_low: C:\ 3.0 of 237 GB free", "cpu_high: 95 % load"]),
            "an alert is told to a model in one short line with the value that crossed the limit");
    }

    private static void HistoryKeepsTheConfiguredRhythmAndPeriod(Checks checks, string state)
    {
        var settings = new NodeVitalsSettings { PollSeconds = 30, HistoryIntervalSeconds = 600, HistoryDays = 1 };
        var history = new NodeVitalsHistory(state);
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        history.Record("office-pc", Sample(), NodeAlerts.None, settings, start);
        history.Record("office-pc", Sample(), NodeAlerts.None, settings, start.AddMinutes(5));
        history.Record("office-pc", Sample(), NodeAlerts.None, settings, start.AddMinutes(10));
        string file = history.FileOf("office-pc");
        checks.That(File.ReadAllLines(file).Length == 2, "a sample is written once per history interval, not on every poll");
        checks.That(OperatingSystem.IsWindows() || File.GetUnixFileMode(file) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
            "the history file is private to the master's user");

        history.Record("office-pc", Sample(), NodeAlerts.None, settings, start.AddDays(1).AddHours(2));
        checks.That(File.ReadAllLines(file).Length == 1, "samples older than the kept period are removed");
        history.Record("../escape", Sample(), NodeAlerts.None, settings, start);
        history.Record("laptop", Sample(), NodeAlerts.None, settings with
        {
            HistoryDays = 0
        }, start);
        checks.That(Directory.GetFiles(Path.GetDirectoryName(file)!).Length == 1,
            "an invalid node name or a history of zero days writes nothing");
    }

    private static void HistorySummaryNamesThePeaks(Checks checks, string state)
    {
        var settings = new NodeVitalsSettings { PollSeconds = 30, HistoryIntervalSeconds = 60, HistoryDays = 7 };
        var history = new NodeVitalsHistory(state);
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        history.Record("office-pc", Sample(), NodeAlerts.None, settings, start);
        NodeVitals peak = Sample() with
        {
            CpuLoadPercent = 97,
            MemoryAvailableBytes = Gigabyte / 2
        };
        history.Record("office-pc", peak, NodeAlerts.CpuHigh | NodeAlerts.MemoryLow, settings, start.AddMinutes(1));
        history.Record("office-pc", Sample() with
        {
            DiskFreeBytes = 2 * Gigabyte
        }, NodeAlerts.DiskLow, settings, start.AddMinutes(2));

        JsonObject summary = history.Summary("office-pc", TimeSpan.FromHours(24), start.AddMinutes(3));
        checks.That(summary["samples"]!.GetValue<int>() == 3, "the summary counts the samples of the period");
        checks.That(summary["cpu_load_percent"]!["max"]!.GetValue<double>() == 97
            && summary["cpu_load_percent"]!["at"]!.ToString() == "2026-10-01T12:01:00+00:00",
            "the highest load is named with its time");
        checks.That(summary["memory_available_bytes"]!["min"]!.GetValue<long>() == Gigabyte / 2
            && summary["disk_free_bytes"]!["min"]!.GetValue<long>() == 2 * Gigabyte
            && summary["disk_free_bytes"]!["at"]!.ToString() == "2026-10-01T12:02:00+00:00",
            "the least free memory and disk are named with their time");
        checks.That(summary["alerts"]!["cpu_high"]!.GetValue<int>() == 1 && summary["alerts"]!["disk_low"]!.GetValue<int>() == 1,
            "the summary counts how often each limit was crossed");
        JsonObject recent = history.Summary("office-pc", TimeSpan.FromMinutes(1.5), start.AddMinutes(3));
        checks.That(recent["samples"]!.GetValue<int>() == 1, "only samples within the asked period count");
        checks.That(history.Summary("laptop", TimeSpan.FromHours(1), start)["samples"]!.GetValue<int>() == 0,
            "a node without history has an empty summary");
    }

    private static void LimitsAreReadFromTheMasterFile(Checks checks)
    {
        NodeVitalsSettings? read = NodeVitalsSettings.TryParse("{\"memory_free_percent\": 20, \"cpu_load_percent\": 75}");
        checks.That(read == new NodeVitalsSettings { MemoryFreePercent = 20, CpuLoadPercent = 75 },
            "the file sets the limits it names and keeps the defaults of the others");
        NodeVitalsSettings? history = NodeVitalsSettings.TryParse(
            "{\"poll_seconds\": 120, \"history_interval_seconds\": 60, \"history_days\": 30}");
        checks.That(history is { PollSeconds: 120, HistoryDays: 30 } && history.HistoryInterval == TimeSpan.FromMinutes(2),
            "history is never written more often than the master asks the node");
        checks.That(NodeVitalsSettings.TryParse("{\"poll_seconds\": 1}") == null && NodeVitalsSettings.TryParse("{\"limit\": 1}") == null,
            "a poll faster than ten seconds or an unknown name is refused");
        checks.That(NodeVitalsSettings.TryParse("{\"memory_free_percent\": \"low\"}") == null
            && NodeVitalsSettings.TryParse("[]") == null && NodeVitalsSettings.TryParse("{\"cpu_load_percent\": 150}") == null,
            "a malformed or out-of-range file is refused rather than half applied");
    }

    private static List<string> NodeLines(ControllerNodeRow node)
    {
        ControllerTrayView view = ControllerTrayMenu.Build(new ControllerStatus(BotSwitch.On, [node], 0, [], 0), [], "");
        return view.Entries.Where(entry => entry.Kind == ControllerTrayEntryKind.Information || entry.Indented)
            .Select(entry => (entry.Indented ? "> " : "") + entry.Text)
            .ToList();
    }

    private static void TrayShowsVitalsInGerman(Checks checks)
    {
        List<string> lines = NodeLines(new ControllerNodeRow("office-pc", true, "0.2610.0604", Sample(), 4));

        checks.That(lines.Contains("> Windows 11 (Build 22621), läuft seit 3 T 4 h"), "the system and its uptime are one line");
        checks.That(lines.Contains(@"> CPU 12 % · RAM 3,1 von 8,0 GB frei · C:\ 41 von 237 GB frei"),
            "load, memory and disk use German decimals");
        checks.That(lines.Contains("> Letzter Kontakt: gerade eben"), "a node that just answered says so without a ticking number");
        var partial = new NodeVitals("Ubuntu 26.04 LTS", null, null, "/", 5 * Gigabyte / 2, 20 * Gigabyte, null, null);
        List<string> partialLines = NodeLines(new ControllerNodeRow("lab", true, "", partial, 70));
        checks.That(partialLines.Contains("> Ubuntu 26.04 LTS") && partialLines.Contains("> / 2,5 von 20 GB frei")
            && partialLines.Contains("> Letzter Kontakt vor 1 min"),
            "unmeasured values are left out instead of guessed");
    }

    private static void TrayShowsLastContactOfAnOfflineNode(Checks checks)
    {
        List<string> offline = NodeLines(new ControllerNodeRow("laptop", false, "", Sample(), 2 * 3600 + 300));
        checks.That(offline.Contains("○ laptop: nicht verbunden") && offline.Contains("> Letzter Kontakt vor 2 h 5 min"),
            "an offline node shows when it was last heard from");
        checks.That(!offline.Any(line => line.Contains("CPU", StringComparison.Ordinal)), "an offline node shows no stale load");
        List<string> unknown = NodeLines(new ControllerNodeRow("laptop", false, "", null, null));
        checks.That(unknown.Contains("> Letzter Kontakt: keiner seit dem Start des Masters"),
            "a node not heard from since the master started says so");
    }

    private static void TrayMarksCrossedLimits(Checks checks)
    {
        List<string> lines = NodeLines(new ControllerNodeRow("office-pc", true, "", Sample(), 4, NodeAlerts.DiskLow));
        checks.That(lines.Contains(@"> ⚠ CPU 12 % · RAM 3,1 von 8,0 GB frei · C:\ 41 von 237 GB frei"),
            "the load line is marked when the master reports a crossed limit");
    }

    private static void TrayShowsVitalsInEnglish(Checks checks)
    {
        List<string> lines = NodeLines(new ControllerNodeRow("office-pc", true, "0.2610.0604", Sample(), 4));

        checks.That(lines.Contains("> Windows 11 (Build 22621), up 3 d 4 h")
            && lines.Contains(@"> CPU 12 % · RAM 3.1 of 8.0 GB free · C:\ 41 of 237 GB free")
            && lines.Contains("> Last contact: just now"),
            "the same lines in English use English decimals");
    }
}
