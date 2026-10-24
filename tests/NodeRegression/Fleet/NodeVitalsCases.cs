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

    public static int Run()
    {
        var checks = new Checks("Node vital signs");
        UiLanguage previous = UiTexts.Language;
        try
        {
            LinuxSourcesAreParsed(checks);
            CpuLoadIsTheBusyShareBetweenSamples(checks);
            NodeValuesAreBounded(checks);
            StatusCarriesVitalsAndContact(checks);
            UiTexts.Use(UiLanguage.German);
            TrayShowsVitalsInGerman(checks);
            TrayShowsLastContactOfAnOfflineNode(checks);
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
        ControllerNodeRow[] nodes = [new("office-pc", true, "0.2610.0604", Sample(), 4), new("laptop", false, "", null, null)];
        var status = new ControllerStatus(BotSwitch.On, nodes, 0, [], 0);
        bool read = ControllerStatus.TryRead(status.ToJson(), out ControllerStatus again);
        checks.That(read && again.Nodes[0] == nodes[0] && again.Nodes[1] == nodes[1],
            "controller-status carries each node's vitals and seconds since last contact, or that they are unknown");
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

    private static void TrayShowsVitalsInEnglish(Checks checks)
    {
        List<string> lines = NodeLines(new ControllerNodeRow("office-pc", true, "0.2610.0604", Sample(), 4));

        checks.That(lines.Contains("> Windows 11 (Build 22621), up 3 d 4 h")
            && lines.Contains(@"> CPU 12 % · RAM 3.1 of 8.0 GB free · C:\ 41 of 237 GB free")
            && lines.Contains("> Last contact: just now"),
            "the same lines in English use English decimals");
    }
}
