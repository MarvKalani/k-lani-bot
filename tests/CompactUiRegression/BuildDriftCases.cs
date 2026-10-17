using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// A known build without a cartridge, while build 1.0 of the same program has one, gets a probe report on its ui_info row. The
// report sends no input, changes no stored document and never makes a function of build 1.0 callable for the new build.
internal static class BuildDriftCases
{
    private const string AbsentFunctionRefusal = "Selected generated function is absent or unverified.";
    private const string NewBuild = "2.0";

    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-build-drift-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        ReadAfterMutationIsLeftToTestRun();
        AmbiguousWindowLeavesUnchecked();
        try
        {
            await UnchangedTargetsNeedTestRunAsync(root);
            await MissingTargetNeedsRepairAsync(root);
            await ChangedTypeNeedsRepairAsync(root);
            await ReadFunctionIsCheckedAsync(root);
            await AbsentWindowLeavesUncheckedAsync(root);
            await UnknownBuildAndOtherVariantHaveNoReportAsync(root);
            await OwnCartridgeHasNoReportAsync(root);
            await DamagedOtherBuildKeepsInfoAsync(root);
            await DetailNamesStoredSelectorAsync(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // Every stored target resolves in build 2.0: the function needs a test run, stays uncallable, and nothing was written or sent.
    private static async Task UnchangedTargetsNeedTestRunAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = await TeachedAsync(client, root, "test-run");
        string before = DocumentHash(roots);
        client.Build = NewBuild;
        int mutations = client.Mutations.Count;
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        var info = await session.CallAsync("ui_info", new(), CancellationToken.None);
        var row = Row(info, 100);
        var report = OtherBuild(row);
        Require(report["build"]?.ToString() == "1.0" && Names(report, "test_run_needed").Contains("write_current"),
            "a function whose stored targets all resolve in the new build needs a test run");
        Require(!row.ContainsKey("functions") && info["next"]!.ToString().Contains(CompactBuildDriftNames.Hint, StringComparison.Ordinal),
            "the report lists no callable function and the reply explains the lists");
        var call = await session.CallAsync("cartridge_call", CompactGatewayCases.Reuse(null, "must not appear"), CancellationToken.None);
        Require(call["success"]?.ToString() == "false" && call["error"]?.ToString() == AbsentFunctionRefusal
            && client.Field != "must not appear", "a function of build 1.0 stays refused for build 2.0");
        Require(client.Mutations.Count == mutations && DocumentHash(roots) == before,
            "the probe sends no input and leaves the stored document of build 1.0 unchanged");
    }

    // The field the function writes is gone in build 2.0, so its first step needs repair and is named.
    private static async Task MissingTargetNeedsRepairAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = await TeachedAsync(client, root, "repair");
        client.Build = NewBuild;
        client.Dialog = false;
        var entry = await RepairEntryAsync(client, roots, "write_current");
        Require(entry["step"]?.GetValue<int>() == 1 && entry["label"]?.ToString() == "write Field"
            && entry["reason"]?.ToString() == "target_absent_or_ambiguous", "a removed target names its step, label and reason");
    }

    // The automation ID now belongs to a button: the stored edit target changed type.
    private static async Task ChangedTypeNeedsRepairAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = await TeachedAsync(client, root, "type");
        client.Build = NewBuild;
        client.WrongRole = true;
        var entry = await RepairEntryAsync(client, roots, "write_current");
        Require(entry["reason"]?.ToString() == "target_type_changed", "a target whose type changed needs repair");
    }

    // A read-only function is probed too; a moved read target is a repair, not an empty pass.
    private static async Task ReadFunctionIsCheckedAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = CompactGatewayCases.WindowRoots(root, "read");
        using (var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25"))
        {
            await session.CallAsync("ui_info", new(), CancellationToken.None);
            string reference = await CompactGatewayCases.FieldAsync(session);
            var learned = await session.CallAsync("cartridge_call", LearnRead(reference), CancellationToken.None);
            Require(learned["success"]?.ToString() == "true", "the read function is learned for build 1.0");
        }
        client.Build = NewBuild;
        client.Dialog = false;
        var entry = await RepairEntryAsync(client, roots, "read-field");
        Require(entry["label"]?.ToString() == "read Field", "a read function whose target moved names its read step");
    }

    // The stored step belongs to a main window, but build 2.0 shows only a dialog: the function stays unchecked, not repaired.
    private static async Task AbsentWindowLeavesUncheckedAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = await TeachedAsync(client, root, "window");
        client.Build = NewBuild;
        client.WindowKind = "dialog";
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        var info = await session.CallAsync("ui_info", new(), CancellationToken.None);
        var entry = Entries(OtherBuild(Row(info, 100)), "unchecked").Single(item => item["name"]?.ToString() == "write_current");
        Require(entry["window"]?.ToString() == "main" && entry["reason"]?.ToString() == "window_absent",
            "a step whose window kind is not open leaves the function unchecked with that window named");
    }

    // An unknown build proves no program, and another process variant is another program: neither gets a report.
    private static async Task UnknownBuildAndOtherVariantHaveNoReportAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = await TeachedAsync(client, root, "unknown");
        client.Build = "unknown";
        using (var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25"))
        {
            var info = await session.CallAsync("ui_info", new(), CancellationToken.None);
            Require(!Row(info, 100).ContainsKey("other_builds") && info["next"]?.ToString() == CompactViewNames.StateHint,
                "an unknown build gets no report");
        }
        client.Build = "1.0";
        client.DuplicateWindow = true;
        client.SecondProcess = true;
        client.SecondProcessName = "other-editor";
        client.SecondBuild = NewBuild;
        using var mixed = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        var rows = await mixed.CallAsync("ui_info", new(), CancellationToken.None);
        Require(!Row(rows, 101).ContainsKey("other_builds") && !Row(rows, 100).ContainsKey("other_builds"),
            "another process variant and the qualified build itself get no report");
    }

    // A build with its own stored document has a qualified cartridge; its listing applies and no drift report is added.
    private static async Task OwnCartridgeHasNoReportAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = await TeachedAsync(client, root, "own");
        client.Build = NewBuild;
        await TeachAsync(client, roots, "write_new");
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        var info = await session.CallAsync("ui_info", new(), CancellationToken.None);
        Require(!Row(info, 100).ContainsKey("other_builds") && Row(info, 100).ContainsKey("functions"),
            "a build with its own cartridge lists its functions and no report");
    }

    // A damaged document of build 1.0 leaves ui_info answering without a report.
    private static async Task DamagedOtherBuildKeepsInfoAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = await TeachedAsync(client, root, "damaged");
        File.WriteAllText(Directory.GetFiles(roots.Library, "generated-ui.json", SearchOption.AllDirectories).Single(), "{ damaged");
        client.Build = NewBuild;
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        var info = await session.CallAsync("ui_info", new(), CancellationToken.None);
        Require(info["success"]?.ToString() == "true" && !Row(info, 100).ContainsKey("other_builds"),
            "a damaged other-build document leaves ui_info answering without a report");
    }

    // The evidence reference reads back the per-step detail with the stored selector, through the existing result route.
    private static async Task DetailNamesStoredSelectorAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        var roots = await TeachedAsync(client, root, "detail");
        client.Build = NewBuild;
        client.Dialog = false;
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        var info = await session.CallAsync("ui_info", new(), CancellationToken.None);
        string evidence = OtherBuild(Row(info, 100))["evidence_ref"]!.ToString();
        var repeated = await session.CallAsync("ui_info", new(), CancellationToken.None);
        Require(OtherBuild(Row(repeated, 100))["evidence_ref"]!.ToString() == evidence,
            "an unchanged report keeps its evidence reference, so repeated ui_info calls do not fill the result store");
        var page = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "Info.result",
            ["arguments"] = new JsonObject { ["evidence_ref"] = evidence, ["section"] = "debug" }
        }, CancellationToken.None);
        var detail = JsonNode.Parse(page["text"]!.ToString())!;
        var function = detail["builds"]![0]!["functions"]!.AsArray().Single(item => item!["name"]!.ToString() == "write_current")!;
        Require(detail["input_sent"]?.GetValue<bool>() == false && detail["current"]?["build"]?.ToString() == NewBuild
            && function["step"]?["automation_id"]?.ToString() == "field" && function["step"]?["role"]?.ToString() == "Edit",
            "the detail names the current build and the stored selector of the step to repair");
    }

    // A read after a write sees the state the write creates, so its absent target before running is left to the test run.
    private static void ReadAfterMutationIsLeftToTestRun()
    {
        var draft = ProbeDraft(WriteField(),
            new CompactRecipeStep(CompactRecipeAction.Read, ProbeSelector("Result"), CompactRecipeRoute.NativeRead));
        var window = ProbeWindow(100, CompactWindowKind.Main);
        var assessed = CompactBuildDriftProbe.Assess(draft, new CompactDriftView(window, [window], ProbeTrees()));
        Require(assessed.Status == CompactDriftStatus.TestRunNeeded && assessed.CheckedSteps == 1 && assessed.RunOnlySteps == 1,
            "a read after a mutation is counted for the test run, not reported as a repair");
    }

    // Two main windows of one instance leave the step's window ambiguous: the function is unchecked, not repaired.
    private static void AmbiguousWindowLeavesUnchecked()
    {
        var draft = ProbeDraft(WriteField());
        var first = ProbeWindow(100, CompactWindowKind.Main);
        var view = new CompactDriftView(first, [first, ProbeWindow(101, CompactWindowKind.Main)], ProbeTrees());
        var assessed = CompactBuildDriftProbe.Assess(draft, view);
        Require(assessed.Status == CompactDriftStatus.Unchecked && assessed.Step?.Reason == CompactDriftReason.WindowAmbiguous,
            "two windows of the step's kind leave the function unchecked with window_ambiguous");
    }

    private static CompactFunctionDraft ProbeDraft(params CompactRecipeStep[] steps)
    {
        return new("probe", new(CompactTopicCategory.Use, "Probe"), steps, [], CompactCaptureValidation.DemonstratedMutation);
    }

    private static CompactRecipeStep WriteField()
    {
        return new(CompactRecipeAction.Write, ProbeSelector("Field"), CompactRecipeRoute.NativeText, "text");
    }

    private static CompactStoredSelector ProbeSelector(string name)
    {
        return new(CompactRole.Parse("Edit"), name, name.ToLowerInvariant(), CompactWindowKind.Main);
    }

    private static CompactNativeWindow ProbeWindow(long handle, CompactWindowKind kind)
    {
        return new(10, "fixture-editor", NewBuild, "Editor", handle, "2026-10-08T00:00:00Z", kind);
    }

    // The main window holds only the written field; the read target appears after the write.
    private static IReadOnlyDictionary<CompactWindowKind, CompactNativeObservation> ProbeTrees()
    {
        CompactTreeNode[] nodes = [new(0, 1, CompactRole.Parse("Window"), "Editor", "", "", true, false),
            new(2, 2, CompactRole.Parse("Edit"), "Field", "field", "old", true, false)];

        return new Dictionary<CompactWindowKind, CompactNativeObservation>
        {
            [CompactWindowKind.Main] = new("s1", nodes, new Dictionary<int, KLaniBot.Platforms.UiFieldMetadata>())
        };
    }

    private static async Task<CompactStorageRoots> TeachedAsync(NativeAdapterFixture client, string root, string name)
    {
        var roots = CompactGatewayCases.WindowRoots(root, name);
        await TeachAsync(client, roots, "write_current");

        return roots;
    }

    private static async Task TeachAsync(NativeAdapterFixture client, CompactStorageRoots roots, string name)
    {
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        string reference = await CompactGatewayCases.FieldInWindowAsync(session, 100);
        var demonstration = CompactGatewayCases.WriteInWindow(reference, "taught " + name, 100);
        var written = await session.CallAsync("ui_act", demonstration, CancellationToken.None);
        var learned = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "Programming.learn",
            ["arguments"] = CompactGatewayCases.Learn(written["evidence"]!.ToString(), reference, name)
        }, CancellationToken.None);
        Require(learned["success"]?.ToString() == "true", "the function " + name + " is learned: " + learned.ToJsonString());
    }

    private static async Task<JsonObject> RepairEntryAsync(NativeAdapterFixture client, CompactStorageRoots roots, string name)
    {
        using var session = new CompactGatewaySession(client, "fixture", roots, () => "2025-11-25");
        var info = await session.CallAsync("ui_info", new(), CancellationToken.None);
        var entries = Entries(OtherBuild(Row(info, 100)), "repair_needed");

        return entries.Single(item => item["name"]?.ToString() == name);
    }

    private static JsonObject LearnRead(string reference)
    {
        return new JsonObject
        {
            ["function"] = "Programming.learn_read",
            ["arguments"] = new JsonObject
            {
                ["pid"] = 10,
                ["name"] = "read-field",
                ["topic"] = "Use/Reads",
                ["ref"] = reference,
                ["field"] = "value",
                ["output"] = "text"
            }
        };
    }

    private static JsonObject OtherBuild(JsonObject row)
    {
        Require(row["other_builds"] is JsonArray { Count: 1 }, "the row carries one other-build report");

        return row["other_builds"]![0]!.AsObject();
    }

    private static string[] Names(JsonObject report, string list)
    {
        return (report[list] as JsonArray ?? []).Select(item => item is JsonObject entry ? entry["name"]!.ToString() : item!.ToString())
            .ToArray();
    }

    private static JsonObject[] Entries(JsonObject report, string list)
    {
        return (report[list] as JsonArray ?? []).OfType<JsonObject>().ToArray();
    }

    private static string DocumentHash(CompactStorageRoots roots)
    {
        string file = Directory.GetFiles(roots.Library, "generated-ui.json", SearchOption.AllDirectories).Single();

        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    }

    private static JsonObject Row(JsonObject info, long window)
    {
        return info["windows"]!.AsArray().OfType<JsonObject>().Single(row => row["window"]!.GetValue<long>() == window);
    }

    private static void Require(bool condition, string name)
    {
        CompactGatewayCases.Require(condition, name);
    }
}
