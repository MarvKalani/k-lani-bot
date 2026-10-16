using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Cartridges;
using KLaniBot.Fleet;
using KLaniBot.Inventory;
using KLaniBot.Platforms.Linux;

// The application scanner decides what an administrator is told about installed software and which
// cartridge to trust for it. A wrong match would suggest a cartridge for a program it was never tried on,
// so the matching rules are tested one by one, and the scan modes are tested for what they must not reveal.
int passed = 0;

void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new Exception("Application scanner: " + name);
    }
    passed++;
}

string root = RepositoryLocator.Root();
string temporary = Path.Combine(Path.GetTempPath(), "k-lani-bot-scanner-" + Guid.CreateVersion7());
Directory.CreateDirectory(temporary);
try
{
    CheckVersions();
    CheckNames();
    CheckDesktopEntries();
    CheckKindClassifier();
    CheckRepositoryCatalogues();
    CheckRecommendations();
    CheckScanModes();
    CheckCommand();
    CheckThisMachine();
}
finally
{
    Directory.Delete(temporary, true);
}
Console.WriteLine($"{passed} application scanner checks passed.");

void CheckVersions()
{
    Check(
        ApplicationVersion.Parse("1:7.5.1-0ubuntu2").Components.SequenceEqual([7, 5, 1]),
        "a Debian epoch and revision do not belong to the program version");
    Check(ApplicationVersion.Parse("16.0.17328.20162").Components.Length == 4, "a four part Windows version is kept whole");
    Check(ApplicationVersion.Parse("v2.4.2").Components.SequenceEqual([2, 4, 2]), "a leading v is ignored");
    Check(
        !ApplicationVersion.Parse("").IsKnown && !ApplicationVersion.Parse("rolling").IsKnown,
        "text without a number is an unknown version");
    var cartridge = ApplicationVersion.Parse("2.4.2");
    Check(
        ApplicationVersion.Parse("2.4.2.0").CompareTo(cartridge).Relation == VersionRelation.Same,
        "trailing zeros do not change a version");
    var older = ApplicationVersion.Parse("2.4.1").CompareTo(cartridge);
    Check(
        older.Relation == VersionRelation.SameSeries && older.Direction == VersionDirection.InstalledIsOlder,
        "a previous patch is a neighbour of the same series, installed older");
    var newer = ApplicationVersion.Parse("2.5.0").CompareTo(cartridge);
    Check(
        newer.Relation == VersionRelation.SameMajor && newer.Direction == VersionDirection.InstalledIsNewer,
        "a following minor is a neighbour of the same major, installed newer");
    Check(
        ApplicationVersion.Parse("3.0.0").CompareTo(cartridge).Relation == VersionRelation.DifferentMajor,
        "another major version is not a neighbour");
    Check(
        ApplicationVersion.Parse("rolling").CompareTo(cartridge).Relation == VersionRelation.Unknown,
        "an unknown version relates to nothing");
}

void CheckNames()
{
    Check(TargetMatcher.NameMatches("Notepad++", "Notepad++ (64-bit x64)"), "architecture words after the name are ignored");
    Check(TargetMatcher.NameMatches("OrcaSlicer", "OrcaSlicer 2.4.2"), "a version after the name is ignored");
    Check(TargetMatcher.NameMatches("Claude", "Claude 0.9.1 (x64)"), "name with version and architecture");
    Check(!TargetMatcher.NameMatches("Claude", "Claude Code"), "another product that starts with the same word is not a match");
    Check(!TargetMatcher.NameMatches("OrcaSlicer", "Orca"), "the Orca screen reader is not OrcaSlicer");
    Check(!TargetMatcher.NameMatches("Notepad", "Notepad++"), "the plus signs belong to the name");
    Check(!TargetMatcher.NameMatches("", "Anything"), "an empty name matches nothing");
    var target = new ApplicationTarget("word", "Microsoft Word", ["Word"], TargetDetection.None with
    {
        Executables = ["WINWORD.EXE"]
    }, TargetDetection.None, TargetDetection.None, [], "");
    var byExecutable = new ApplicationEvidence("Microsoft Office 365", "16.0", "Microsoft", "app-paths", ["winword.exe"]);
    Check(
        TargetMatcher.TryMatch(target, "windows", byExecutable, out MatchBasis basis) && basis == MatchBasis.Identifier,
        "an executable name matches regardless of case, even when the display name does not");
    Check(!TargetMatcher.TryMatch(target, "linux", byExecutable, out _), "hints of another operating system are not used");
}

void CheckDesktopEntries()
{
    string entry =
        "[Desktop Entry]\nType=Application\nName=Tool\nName[de]=Werkzeug\nExec=env A=1 /opt/tool/bin/tool --flag "
        + "%U\nCategories=Office;Spreadsheet;\nX-AppImage-Version=3.2.1\n[Desktop Action new]\nName=New window\n";
    var parsed = DesktopEntryParser.Parse("tool.desktop", entry);
    Check(
        parsed != null && parsed.Name == "Tool" && parsed.AppImageVersion == "3.2.1" && parsed.Categories.Length == 2,
        "the unlocalised name, categories and the AppImage version are read");
    Check(
        DesktopEntryParser.Parse("a.desktop", "[Desktop Entry]\nType=Application\nName=Hidden\nNoDisplay=true\n") == null,
        "an entry that is not shown to a person is skipped");
    Check(
        DesktopEntryParser.Parse("b.desktop", "[Desktop Entry]\nType=Link\nName=Link\n") == null,
        "an entry that is not an application is skipped");
    Check(
        DesktopEntryParser.Parse("c.desktop", "[Desktop Action x]\nName=Only an action\nType=Application\n") == null,
        "keys outside the Desktop Entry group are ignored");
    Check(DesktopEntryParser.ExecutableName("env A=1 /opt/tool/bin/tool --flag %U") == "tool", "env prefixes and options are skipped");
    Check(
        DesktopEntryParser.ExecutableName("flatpak run --branch=stable org.example.App") == "org.example.App",
        "a flatpak launcher names the application id");
    Check(DesktopEntryParser.ExecutableName("\"/opt/My Tool/tool\" %f") == "tool", "quoted paths with spaces are handled");
    Check(
        DesktopEntryParser.ExecutablePath("/opt/tool/bin/tool %U") == "/opt/tool/bin/tool"
            && DesktopEntryParser.ExecutablePath("tool %U") == "",
        "only an absolute path counts as a location");
}

void CheckKindClassifier()
{
    Check(
        ApplicationKindClassifier.Classify("Microsoft Visual C++ 2015-2022 Redistributable (x64)") == ApplicationKind.Component,
        "a redistributable is a component");
    Check(
        ApplicationKindClassifier.Classify("Security Update for Microsoft Office (KB5002345)") == ApplicationKind.Component,
        "an update package is a component");
    Check(ApplicationKindClassifier.Classify("NVIDIA Graphics Driver 551.23") == ApplicationKind.Component, "a driver is a component");
    Check(ApplicationKindClassifier.Classify("Notepad++ (64-bit x64)") == ApplicationKind.Application, "an editor is an application");
}

void CheckRepositoryCatalogues()
{
    var offers = CartridgeCatalogue.Load(Path.Combine(root, "cartridges"));
    var orca = offers.Single(offer => offer.Application == "OrcaSlicer");
    Check(
        orca.CartridgeId == "k-lani-bot.orcaslicer"
            && orca.ApplicationVersion!.Raw == "2.4.2"
            && orca.Platform == "linux"
            && orca.Architecture == "x64",
        "the OrcaSlicer cartridge is read with version and platform");
    Check(orca.Validation == CartridgeValidation.RealAppTested, "a real-application test counts");
    Check(orca.EntryPoints.SequenceEqual(["inspect-ui", "import-stl"]), "an entrypoint that only exercises a test double is not offered");
    Check(
        offers.All(offer => offer.Application != "apple-macos-27-de-install"),
        "the operating system installation cartridge is not a program cartridge");
    var targets = ApplicationTargetCatalogue.Load(Path.Combine(root, "cartridges", "targets.json"));
    Check(
        targets.Select(target => target.Id).Distinct().Count() == targets.Count && targets.Count >= 8,
        "the shipped target catalogue loads with unique ids");
    foreach (var offer in offers)
    {
        Check(targets.Any(target => target.KnownNames().Any(name =>
                TargetMatcher.NameMatches(offer.Application, name) || TargetMatcher.NameMatches(name, offer.Application))),
            "every shipped cartridge belongs to a catalogued target: " + offer.Application);
    }
}

CartridgeOffer Offer(
    string application,
    string testedVersion,
    string platform = "linux",
    string architecture = "x64",
    CartridgeValidation validation = CartridgeValidation.RealAppTested)
{
    return new CartridgeOffer(
        "test." + application.ToLowerInvariant(),
        "1.0.0",
        application,
        ApplicationVersion.Parse(testedVersion),
        null,
        null,
        platform,
        architecture,
        validation,
        false,
        ["main"],
        "x");
}

void CheckRecommendations()
{
    var machine = new ScanMachineInfo("pc", "linux", "x", "X64");
    ScanRecommendation? Recommend(CartridgeOffer offer, string installed)
        => CartridgeMatcher.Best([offer], ApplicationVersion.Parse(installed), machine)?.Recommendation;
    Check(Recommend(Offer("App", "2.4.2"), "2.4.2") == ScanRecommendation.UseCartridge, "exact version, same system, real test: usable");
    Check(
        Recommend(Offer("App", "2.4.2", validation: CartridgeValidation.FixtureTested), "2.4.2")
            == ScanRecommendation.CheckThenUseCartridge,
        "exact version but only a test double: check first");
    Check(Recommend(Offer("App", "2.4.2"), "2.4.1") == ScanRecommendation.TryNeighbourVersion, "a previous patch: try the neighbour");
    Check(Recommend(Offer("App", "2.4.2"), "2.6.0") == ScanRecommendation.TryNeighbourVersion, "a following minor: try the neighbour");
    Check(Recommend(Offer("App", "2.4.2"), "4.0.0") == ScanRecommendation.AuthorCartridge, "another major version: write a new cartridge");
    Check(
        Recommend(Offer("App", "2.4.2"), "") == ScanRecommendation.DetermineVersionFirst,
        "an unknown installed version: find it out first");
    Check(
        Recommend(Offer("App", "2.4.2", platform: "windows"), "2.4.2") == ScanRecommendation.CheckOtherPlatformCartridge,
        "a cartridge for another system is not used unchecked");
    Check(
        Recommend(Offer("App", "2.4.2", architecture: "arm64"), "2.4.2") == ScanRecommendation.CheckOtherPlatformCartridge,
        "a cartridge for another processor architecture is not used unchecked");

    var range = new CartridgeOffer(
        "range",
        "1.0.0",
        "App",
        null,
        ApplicationVersion.Parse("3.0"),
        ApplicationVersion.Parse("4.0"),
        "linux",
        "any",
        CartridgeValidation.Unreviewed,
        false,
        [],
        "x");
    Check(
        CartridgeMatcher.FitOf(range, ApplicationVersion.Parse("3.5.1")).Fit == CartridgeFit.WithinDeclaredRange,
        "a version inside the declared range fits");
    Check(
        CartridgeMatcher.FitOf(range, ApplicationVersion.Parse("4.0.0")).Fit != CartridgeFit.WithinDeclaredRange,
        "the upper bound is exclusive");

    var older = Offer("App", "2.0.0");
    var closer = Offer("App", "2.4.0") with
    {
        CartridgeId = "closer"
    };
    var best = CartridgeMatcher.Best([older, closer], ApplicationVersion.Parse("2.4.1"), machine);
    Check(
        best!.Offer.CartridgeId == "closer" && best.OtherOffers == 1,
        "of several cartridges the one closest to the installed version is chosen");
    var wrongSystem = Offer("App", "2.4.1", platform: "windows") with
    {
        CartridgeId = "windows"
    };
    var rightSystem = Offer("App", "2.9.0") with
    {
        CartridgeId = "linux"
    };
    Check(
        CartridgeMatcher.Best([wrongSystem, rightSystem], ApplicationVersion.Parse("2.4.1"), machine)!.Offer.CartridgeId == "linux",
        "the same system beats a closer version on another system");
}

IReadOnlyList<ApplicationTarget> SampleTargets()
{
    string file = Path.Combine(temporary, "targets-sample.json");
    File.WriteAllText(file, """
        {
          "format": "k-lani-bot.app-targets.v1",
          "targets": [
            { "id": "notepad-plus-plus", "name": "Notepad++", "windows": { "display_names": ["Notepad++"] }, "routes": ["command_line"] },
            { "id": "word", "name": "Microsoft Word", "aliases": ["Word"],
              "windows": { "executables": ["WINWORD.EXE"] }, "routes": ["com", "registry"] },
            { "id": "claude-desktop", "name": "Claude Desktop", "aliases": ["Claude"], "windows": { "display_names": ["Claude"] } }
          ]
        }
        """);
    return ApplicationTargetCatalogue.Load(file);
}

void CheckScanModes()
{
    var targets = SampleTargets();
    Check(targets[1].Routes.SequenceEqual(["com", "registry"]), "control routes are read from the target catalogue");
    var offers = new[] { Offer("Notepad++", "8.6.0", platform: "windows") };
    var machine = new ScanMachineInfo("pc", "windows", "11", "X64");
    var installed = new[]
    {
        new ApplicationEvidence("Notepad++ (64-bit x64)", "8.6.0", "Notepad++ Team", "HKLM", ["Notepad++"]),
        new ApplicationEvidence("Secret Hobby App", "1.0", "Someone", "HKLM", ["secret"]),
        new ApplicationEvidence(
            "Microsoft Visual C++ 2022 Redistributable",
            "14.38",
            "Microsoft",
            "HKLM",
            ["vc"],
            ApplicationKind.Component)
    };
    var inventory = AppScanner.Scan(machine, ScanMode.Inventory, installed, targets, offers, new HashSet<string> { "notepad++" }, []);
    Check(inventory.Rows.Length == 3, "the inventory mode reports every installed entry");
    var notepad = inventory.Rows.Single(row => row.TargetId == "notepad-plus-plus");
    Check(
        notepad.Match?.Recommendation == ScanRecommendation.UseCartridge && notepad.Match.Fit == CartridgeFit.Exact,
        "the editor is matched to its cartridge exactly");
    Check(
        notepad.Running == true && inventory.Rows.Single(row => row.Name == "Secret Hobby App").Running == false,
        "a program is running when a process carries its executable name");
    Check(
        inventory.TargetsNotFound.Select(missing => missing.TargetId).Order().SequenceEqual(["claude-desktop", "word"]),
        "targets that are not installed are listed");
    Check(
        inventory.Rows.First().Kind == ApplicationKind.Application && inventory.Rows.Last().Kind == ApplicationKind.Component,
        "components are sorted behind the applications");

    var targetsOnly = AppScanner.Scan(machine, ScanMode.TargetsOnly, installed, targets, offers, null, []);
    string serialized = JsonSerializer.Serialize(targetsOnly);
    Check(targetsOnly.Rows.Length == 1 && !serialized.Contains("Secret Hobby App", StringComparison.Ordinal) && !serialized.Contains(
        "Redistributable",
        StringComparison.Ordinal),
        "the targets-only mode reports nothing about programs outside the catalogue");

    var declared = AppScanner.Scan(
        machine,
        ScanMode.Declared,
        DeclaredApplications.Parse("Word, Notepad++ 8.6.1, Claude Desktop"),
        targets,
        offers,
        null,
        []);
    Check(
        declared.Rows.Length == 3 && declared.Rows.All(row => row.Source == "declared" && row.TargetId != null),
        "declared names are matched to targets by name");
    Check(
        declared.Rows.Single(row => row.TargetId == "notepad-plus-plus").Version == "8.6.1",
        "a trailing version in a declared name is taken as the version");
    Check(
        declared.Rows.Single(row => row.TargetId == "notepad-plus-plus").Match?.Fit == CartridgeFit.NeighbourSameSeries,
        "a declared neighbour version is classified");
    var sameVersion = AppScanner.Scan(machine, ScanMode.Declared, DeclaredApplications.Parse("Notepad++ 8.6"), targets, offers, null, []);
    Check(sameVersion.Rows[0].Match?.Fit == CartridgeFit.Exact, "8.6 and 8.6.0 are the same version");
    Check(DeclaredApplications.Parse("A;B\nC").Count == 3, "declared names may be separated by commas, semicolons or lines");
    Check(Throws(() => DeclaredApplications.Parse("  ")), "an empty declaration is refused");
    Check(Throws(() => DeclaredApplications.Parse(new string('x', 200))), "an overlong name is refused");

    string text = AppScanReportText.Render(inventory, showAllRows: false);
    Check(
        text.Contains("Notepad++", StringComparison.Ordinal)
            && text.Contains("→ usable", StringComparison.Ordinal)
            && text.Contains("Microsoft Word", StringComparison.Ordinal),
        "the text report names usable cartridges and missing targets");
    var packaged = new[] { new ApplicationEvidence("Packaged App", "4:26.2.5.2-0ubuntu0.26.04.1", "", "dpkg", ["packaged"]) };
    string packagedText = AppScanReportText.Render(
        AppScanner.Scan(machine, ScanMode.Inventory, packaged, [], [], null, []),
        showAllRows: true);
    Check(
        packagedText.Contains("Packaged App 26.2.5.2", StringComparison.Ordinal)
            && !packagedText.Contains("4:26", StringComparison.Ordinal),
        "package versions are shown without epoch and packaging revision");
    Check(
        !text.Contains("Secret Hobby App", StringComparison.Ordinal)
            && AppScanReportText.Render(inventory, showAllRows: true).Contains("Secret Hobby App", StringComparison.Ordinal),
        "the terminal lists all other programs only on request");
}

void CheckCommand()
{
    string cartridges = Path.Combine(temporary, "cartridges");
    Directory.CreateDirectory(cartridges);
    File.Copy(Path.Combine(temporary, "targets-sample.json"), Path.Combine(cartridges, "targets.json"));
    string bundle = Path.Combine(cartridges, "programs", "notepad", "8.6.0");
    Directory.CreateDirectory(bundle);
    File.WriteAllText(Path.Combine(bundle, "cartridge.json"), """
        {
          "format": "k-lani-bot.local-macro-bundle.v1",
          "id": "test.notepad",
          "version": "1.0.0",
          "target": { "application": "Notepad++", "application_version": "8.6.0", "tested_platform": "windows-x64" },
          "entrypoints": [ { "name": "open", "validation": "real_app_tested" } ],
          "release_approved": false
        }
        """);
    string reportFile = Path.Combine(temporary, "out", "report.json");
    var output = new StringWriter();
    int exitCode = AppScanCommand.Run(
        [
            "--scan-apps", "--cartridges", cartridges, "--declared", "Notepad++ 8.6.0",
            "--system", "windows", "--architecture", "X64", "--output", reportFile
        ],
        output);
    Check(
        exitCode == 0 && output.ToString().Contains("→ usable", StringComparison.Ordinal),
        "the command scans declared programs against a cartridge directory");
    var report = JsonNode.Parse(File.ReadAllText(reportFile))!;
    Check(
        report["mode"]!.ToString() == "declared" && report["rows"]![0]!["match"]!["recommendation"]!.ToString() == "useCartridge",
        "the report file is written as JSON with named values");
    if (!OperatingSystem.IsWindows())
    {
        Check(
            File.GetUnixFileMode(reportFile) == (UnixFileMode.UserRead | UnixFileMode.UserWrite),
            "the report lists installed software and is stored private");
    }

    var machine = new InventoryMachine("windows", "11", "X64", "X64", 8, "de-DE");
    var applications = new[]
    {
        new InstalledApplication(PairingProtocol.Hash("t", "1"), "Notepad++ (64-bit x64)", "8.6.0", "Team", "HKLM"),
        new InstalledApplication(PairingProtocol.Hash("t", "2"), "Some Other Program", "3.1", "Vendor", "HKLM")
    };
    var inventory = SystemInventory.Create(machine, applications, [new InventorySource("HKLM", true, null)], false);
    string inventoryFile = Path.Combine(temporary, "node-inventory.json");
    File.WriteAllText(inventoryFile, JsonSerializer.Serialize(inventory, SystemInventory.JsonOptions));
    var inventoryOutput = new StringWriter();
    AppScanCommand.Run(["--scan-apps", "--cartridges", cartridges, "--inventory-file", inventoryFile, "--targets-only"], inventoryOutput);
    string inventoryText = inventoryOutput.ToString();
    Check(
        inventoryText.Contains("→ usable", StringComparison.Ordinal)
            && !inventoryText.Contains("Some Other Program", StringComparison.Ordinal),
        "a stored inventory of another node is matched, and targets-only keeps other programs out");
    Check(
        inventoryText.Contains("matched by name only", StringComparison.Ordinal),
        "the report says that a stored inventory is matched by name only");
    File.WriteAllText(inventoryFile, File.ReadAllText(inventoryFile).Replace("8.6.0", "9.9.9"));
    Check(
        Throws(
            () => AppScanCommand.Run(["--scan-apps", "--cartridges", cartridges, "--inventory-file", inventoryFile], new StringWriter())),
        "a tampered stored inventory is refused");
}

void CheckThisMachine()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }
    var source = new LinuxApplicationSource();
    var found = source.Enumerate([]);
    bool anyDesktopEntry = Directory.Exists("/usr/share/applications")
        && Directory.EnumerateFiles("/usr/share/applications", "*.desktop").Any();
    Check(!anyDesktopEntry || found.Count > 0, "the Linux source lists the graphical programs of this machine");
    Check(found.All(item => item.Name.Length > 0 && item.Source.Length > 0), "every listed program has a name and a source");
    Check(source.Limitations.Count > 0, "the source states what it cannot see");
    var inventory = SystemInventoryCollector.Collect();
    inventory.Verify();
    Check(!anyDesktopEntry || inventory.Applications.Length > 0, "the node inventory tool now lists applications on Linux");
    Check(
        inventory.Sources.Any(item => item.Name == "installed-applications" && !item.Complete && item.Limitation != null),
        "and states that this list is not complete");
}

bool Throws(Action action)
{
    try
    {
        action();
        return false;
    }
    catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException)
    {
        return true;
    }
}
