using System.Text.Json.Nodes;
using System.Diagnostics;
using KLaniBot.Actions;
using KLaniBot.Transport.CompactUi;

// Durable library checks use synthetic functions and real private files, including CAS, recovery and immutable legacy loading.
internal static class FunctionLibraryCases
{
    private static readonly CompactProgramIdentity Application = new("fixture", "1.0");

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-function-library-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            InstallAndRestart(Path.Combine(root, "restart"));
            CompareAndSwap(Path.Combine(root, "cas"));
            Interrupted(Path.Combine(root, "before"), CompactRevisionCheckpoint.BeforeReplace);
            Interrupted(Path.Combine(root, "after"), CompactRevisionCheckpoint.AfterReplace);
            LegacyAndUnknown(Path.Combine(root, "legacy"));
            UnsafeFiles(Path.Combine(root, "unsafe"));
            ConcurrentWriters(Path.Combine(root, "concurrent"));
            ExitedWriter(Path.Combine(root, "crashed-before"), "before");
            ExitedWriter(Path.Combine(root, "crashed-after"), "after");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void InstallAndRestart(string root)
    {
        string file = File(root);
        var library = Open(root);
        var automatic = new CompactAutomaticCapture(library);
        var target = new CompactSemanticTarget(10, "Text", CompactRole.Parse("Edit"), "text", CompactWindowKind.Main, []);
        var before = new CompactTreeNode[] { new(0, 1, CompactRole.Parse("Window"), "Fixture", "", "", true, false),
            new(2, 2, CompactRole.Parse("Edit"), "Text", "text", "old", true, false) };
        var after = new CompactTreeNode[] { before[0], before[1] with { Value = "LIVE_SYNTHETIC_INPUT" } };
        var demonstration = new CompactDemonstration(Guid.NewGuid(), 10,
            [new(CompactRecipeAction.Write, target, CompactRecipeRoute.NativeText, "LIVE_SYNTHETIC_INPUT", "field")], before, after);
        var results = automatic.Capture(demonstration, new Dictionary<string, CompactSemanticTarget> { ["field"] = target },
            Current(), new(CompactTopicCategory.Use, "UI"), false);
        Require(results.Single().Verified && library.Revision == 1, "available only after real atomic save");
        string source = System.IO.File.ReadAllText(file);
        Require(!source.Contains("LIVE_SYNTHETIC_INPUT") && !source.Contains("\"pid\":10"), "no captured live data in file");
        var restarted = Open(root);
        Require(restarted.Functions.Count == 1 && restarted.Revision == 1, "restart loads generated function");
        var function = restarted.Functions[0];
        Require(CompactCompatibility.Assess(function.Draft, function.Compatibility, Current()).Status
            == CompactCompatibilityStatus.Reusable, "loaded modern fingerprints preserved");
        Require(!Directory.GetFiles(Path.GetDirectoryName(file)!).Any(path => path.EndsWith(".tmp", StringComparison.Ordinal)),
            "no leftover temporary");
        Require(!Directory.GetFiles(Path.GetDirectoryName(file)!).Any(path => path.EndsWith(".lock", StringComparison.Ordinal)),
            "locks remain private");
        Require(System.IO.File.ReadAllText(file) == source, "loading did not rewrite source");
    }

    private static void CompareAndSwap(string root)
    {
        var first = Open(root);
        var stale = Open(root);
        var draft = Draft("editor.first");
        CompactCompatibility.TryCapture(draft, Current(), out var compatibility);
        Require(first.TryInstall(draft, compatibility.Value!, out _), "first writer");
        string source = System.IO.File.ReadAllText(File(root));
        CompactFunctionDraft other = draft with
        {
            Name = "editor.other"
        };
        CompactCompatibility.TryCapture(other, Current(), out var otherCompatibility);
        Require(!stale.TryInstall(other, otherCompatibility.Value!, out string? error)
            && error == "stale_cartridge_revision", "stale creation cannot overwrite");
        Require(System.IO.File.ReadAllText(File(root)) == source, "CAS retained source");
        System.IO.File.AppendAllText(File(root), " ");
        Require(!first.TryCandidate(new("ui.other", new(CompactTopicCategory.Use, "UI"), CompactRecipeAction.Click,
            draft.Steps[0].Selector, "unknown goal"), out error) && error == "stale_cartridge_revision",
            "byte digest detects external formatting edit");
        Require(first.TryReload(out _) && first.TryCandidate(new("ui.other", new(CompactTopicCategory.Use, "UI"),
            CompactRecipeAction.Click, draft.Steps[0].Selector, "unknown goal"), out _), "reload then save");
        Require(first.Revision == 2 && first.Candidates.Count == 1, "one active file advances revision");
    }

    private static void Interrupted(string root, CompactRevisionCheckpoint boundary)
    {
        string file = File(root);
        string state = State(root);
        CompactRevisionStore.TryOpen(file, state, Application, out var seeded);
        JsonObject initial = Document();
        Require(seeded.Value!.TryReplace(initial, 0, null, out var first), "initial empty document");
        CompactRevisionStore.TryOpen(file, state, Application, out var interrupted, checkpoint =>
        {
            if (checkpoint == boundary)
            {
                throw new IOException("Synthetic interruption.");
            }
        });
        JsonObject next = Document();
        next["candidates"]!.AsArray().Add(Candidate());
        Require(!interrupted.Value!.TryReplace(next, first.Value!.Revision, first.Value.Digest, out _), "write interrupted");
        Require(System.IO.File.Exists(interrupted.Value.JournalPath), "durable intent remains");
        Require(interrupted.Value.TryRecover(out var recovered), "explicit recovery");
        Require(recovered.Value!.Status == (boundary == CompactRevisionCheckpoint.BeforeReplace
            ? CompactRevisionRecovery.PreviousRetained : CompactRevisionRecovery.Committed), "correct recovery outcome");
        long expectedRevision = boundary == CompactRevisionCheckpoint.BeforeReplace ? 1 : 2;
        Require(interrupted.Value.TryRead(out var actual) && actual.Value!.Revision == expectedRevision,
            "actual active revision matches outcome");
        Require(!System.IO.File.Exists(interrupted.Value.JournalPath), "intent cleared after verified recovery");
    }

    private static void LegacyAndUnknown(string root)
    {
        string file = File(root);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        JsonObject document = Document();
        document["version"] = 1;
        document.Remove("revision");
        JsonObject entry = CompactCaptureWire.Draft(Draft("legacy.write"));
        entry["compatibility"] = CompactDiscoveryWire.Compatibility(new("1.0.1", "fixture-platform", "1.0"));
        document["functions"]!.AsArray().Add(entry);
        string legacy = document.ToJsonString();
        System.IO.File.WriteAllText(file, legacy);
        var library = Open(root);
        Require(library.SourceVersion == 1 && library.Revision == 1 && System.IO.File.ReadAllText(file) == legacy,
            "v1 read is nondestructive");
        Require(library.TryCandidate(new("ui.candidate", new(CompactTopicCategory.Use, "UI"), CompactRecipeAction.Click,
            Draft("legacy.write").Steps[0].Selector, "unverified"), out _), "explicit edit migrates format");
        var reloaded = Open(root);
        Require(reloaded.SourceVersion == 2 && reloaded.Revision == 2, "migration has one new revision");
        JsonObject active = JsonNode.Parse(System.IO.File.ReadAllText(file))!.AsObject();
        Require(JsonNode.DeepEquals(active["functions"]![0], entry), "legacy body and schema unchanged");
        active["version"] = 99;
        string future = active.ToJsonString();
        System.IO.File.WriteAllText(file, future);
        Require(!CompactFunctionLibrary.TryOpen(file, State(root), Application, out _), "future format rejected");
        Require(System.IO.File.ReadAllText(file) == future, "future file untouched");
    }

    private static void UnsafeFiles(string root)
    {
        Directory.CreateDirectory(root);
        string file = File(root);
        string directory = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(directory);
        string target = Path.Combine(root, "foreign.json");
        System.IO.File.WriteAllText(target, "{}");
        System.IO.File.CreateSymbolicLink(file, target);
        Require(!CompactFunctionLibrary.TryOpen(file, State(root), Application, out _), "symbolic active file rejected");
        Require(System.IO.File.ReadAllText(target) == "{}", "symbolic target untouched");
        System.IO.File.Delete(file);
        Require(!CompactRevisionStore.TryOpen(file, Path.Combine(directory, "private"), Application, out _),
            "recovery state outside exports");
        CompactRevisionStore.TryOpen(file, State(root), Application, out var store);
        var owner = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
            ["started"] = CompactProcessLifetime.Observe(Environment.ProcessId).Started
        };
        Directory.CreateDirectory(State(root));
        System.IO.File.WriteAllText(store.Value!.LockPath, owner.ToJsonString());
        Require(!store.Value.TryReplace(Document(), 0, null, out var locked) && locked.Error == CompactFunctionError.Locked,
            "live owner is not removed");
        Require(System.IO.File.Exists(store.Value.LockPath), "live lock retained");
    }

    private static CompactFunctionLibrary Open(string root)
    {
        Require(CompactFunctionLibrary.TryOpen(File(root), State(root), Application, out var opened), "open library");

        return opened.Value!;
    }

    private static void ConcurrentWriters(string root)
    {
        var first = Open(root);
        var second = Open(root);
        var one = Draft("editor.one");
        var two = Draft("editor.two");
        CompactCompatibility.TryCapture(one, Current(), out var firstCompatibility);
        CompactCompatibility.TryCapture(two, Current(), out var secondCompatibility);
        using var barrier = new Barrier(2);
        Task<bool> left = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return first.TryInstall(one, firstCompatibility.Value!, out _);
        });
        Task<bool> right = Task.Run(() =>
        {
            barrier.SignalAndWait();
            return second.TryInstall(two, secondCompatibility.Value!, out _);
        });
        Task.WaitAll(left, right);
        Require(left.Result != right.Result, "concurrent CAS commits exactly one writer");
        Require(Open(root).Functions.Count == 1 && Open(root).Revision == 1, "one complete active file");
    }

    private static void ExitedWriter(string root, string boundary)
    {
        CompactRevisionStore.TryOpen(File(root), State(root), Application, out var store);
        Require(store.Value!.TryReplace(Document(), 0, null, out _), "seed child-process transaction");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        {
            start.ArgumentList.Add(typeof(Program).Assembly.Location);
        }
        start.ArgumentList.Add("--revision-crash");
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(boundary);
        using Process child = Process.Start(start)!;
        Task<string> output = child.StandardOutput.ReadToEndAsync();
        Task<string> error = child.StandardError.ReadToEndAsync();
        Require(child.WaitForExit(10000) && child.ExitCode == 17, "controlled writer actually exited at the checkpoint");
        output.GetAwaiter().GetResult();
        error.GetAwaiter().GetResult();
        Require(System.IO.File.Exists(store.Value.LockPath) && System.IO.File.Exists(store.Value.JournalPath),
            "crash leaves lock and intent");
        Require(CompactProcessLifetime.Observe(child.Id).State == CompactProcessState.Dead, "current OS proves owner dead");
        Require(store.Value.TryRecover(out var recovered) && recovered.Value!.Revision == (boundary == "before" ? 1 : 2),
            "recovery handles the durable crash boundary");
        Require(!System.IO.File.Exists(store.Value.LockPath), "dead owner lock removed after recovery");
    }

    public static void CrashChild(string root, string boundary)
    {
        CompactRevisionCheckpoint expected = boundary == "before" ? CompactRevisionCheckpoint.BeforeReplace
            : CompactRevisionCheckpoint.AfterReplace;
        Require(CompactRevisionStore.TryOpen(File(root), State(root), Application, out var store, checkpoint =>
        {
            if (checkpoint == expected)
            {
                Environment.Exit(17);
            }
        }), "child opens real revision store");
        Require(store.Value!.TryRead(out var prior), "child reads current revision");
        JsonObject document = Document();
        document["candidates"]!.AsArray().Add(Candidate());
        store.Value.TryReplace(document, prior.Value!.Revision, prior.Value.Digest, out _);
    }

    private static string File(string root)
    {
        return Path.Combine(root, "export", "programs", "fixture", "1.0", "generated-ui.json");
    }

    private static string State(string root)
    {
        return Path.Combine(root, "private-revisions");
    }

    private static JsonObject Document()
    {
        return new()
        {
            ["format"] = CompactFunctionDocumentCodec.Format,
            ["version"] = 2,
            ["application"] = new JsonObject { ["key"] = Application.Key, ["version"] = Application.Build },
            ["functions"] = new JsonArray(),
            ["candidates"] = new JsonArray()
        };
    }

    private static JsonObject Candidate()
    {
        return new()
        {
            ["name"] = "ui.candidate",
            ["topic"] = "Use/UI",
            ["action"] = "click",
            ["selector"] = CompactDiscoveryWire.Selector(Draft("ui.candidate").Steps[0].Selector),
            ["reason"] = "unverified"
        };
    }

    private static CompactFunctionDraft Draft(string name)
    {
        var selector = new CompactStoredSelector(CompactRole.Parse("Edit"), "Text", "text", CompactWindowKind.Main, []);

        return new(name, new(CompactTopicCategory.Use, "UI"),
            [new(CompactRecipeAction.Write, selector, CompactRecipeRoute.NativeText, "text")],
            [new("value", selector, CompactOutputField.Value, CompactOutputCheckKind.EqualsParameter, "text")],
            CompactCaptureValidation.DemonstratedMutation);
    }

    private static CompactObservedCompatibility Current()
    {
        var schemas = new CompactActionSchemas(new Dictionary<Tool, CompactContractDigest>
        {
            [Tool.GetMachineCapabilities] = new(new string('a', 64)),
            [Tool.ListWindows] = new(new string('a', 64)),
            [Tool.GetUiTree] = new(new string('a', 64)),
            [Tool.TypeText] = new(new string('a', 64))
        }, new Dictionary<string, CompactContractDigest>());

        return new("1.0.1", CompactCompatibilityWire.Wire("2025-06-18"), "fixture-platform", Application, schemas,
            new(new string('b', 64)), new(new string('c', 64)));
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Function library regression failed: " + name);
        }
    }
}
