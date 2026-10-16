using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Transport.CompactUi;

// Signed receipt tests use explicitly synthetic proofs and real private files, never fabricated installed-application acceptance.
internal static class AcceptanceStoreCases
{
    private static readonly CompactProgramIdentity Application = new("receipt-fixture", "1.0");
    private static readonly CompactAcceptanceAuthority Authority = new("isolated-node", "owner", "authorized-fixture-connection");

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-receipt-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            SignedRevalidation(Path.Combine(root, "valid"));
            ForgedAndForeign(Path.Combine(root, "forged"));
            InvalidProofs(Path.Combine(root, "proof"));
            PrivateFiles(Path.Combine(root, "privacy"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void SignedRevalidation(string root)
    {
        var library = Library(root);
        var store = Evidence(root);
        library.TryGet("editor.write", out var selected);
        CompactObservedCompatibility changed = Current() with
        {
            Native = new(new string('d', 64)),
            NodeVersion = "1.0.2"
        };
        Require(CompactCompatibility.Assess(selected.Value!.Draft, selected.Value.Compatibility, changed).Status
            == CompactCompatibilityStatus.NeedsRevalidation, "writer change remains blocked");
        Require(store.TryRecord(selected.Value, changed, Proof(), out var receipt), "isolated proof signs receipt");
        string before = System.IO.File.ReadAllText(FunctionFile(root));
        var acceptance = new CompactRevalidation(library, store);
        Require(acceptance.TryAccept("editor.write", 1, receipt.Value!, Authority.Node, changed, out var accepted)
            && accepted.Value!.Revision == 2, "signed receipt updates one atomic revision");
        var oldEntry = JsonNode.Parse(before)!["functions"]![0]!.AsObject();
        var newEntry = JsonNode.Parse(System.IO.File.ReadAllText(FunctionFile(root)))!["functions"]![0]!.AsObject();
        oldEntry.Remove("compatibility");
        newEntry.Remove("compatibility");
        Require(JsonNode.DeepEquals(oldEntry, newEntry), "function body and schema unchanged");
        library.TryGet("editor.write", out var current);
        Require(CompactCompatibility.Assess(current.Value!.Draft, current.Value.Compatibility, changed).Status
            == CompactCompatibilityStatus.Reusable, "verified changed writer now reusable");
        Require(!store.TryRead(receipt.Value!, current.Value, changed, out var consumed)
            && consumed.Error == CompactAcceptanceError.ForeignOrStaleEvidence, "old receipt bound to previous compatibility");
        Require(!acceptance.TryAccept("editor.write", 1, receipt.Value!, Authority.Node, changed, out var stale)
            && stale.Error == CompactAcceptanceError.StaleRevision, "revision check before receipt reuse");
        string source = System.IO.File.ReadAllText(Path.Combine(store.Root, receipt.Value!.Value + ".json"));
        Require(!source.Contains("parameters") && !source.Contains("training") && !source.Contains("snapshot"),
            "receipt contains digests only");
    }

    private static void ForgedAndForeign(string root)
    {
        var library = Library(root);
        var store = Evidence(root);
        library.TryGet("editor.write", out var selected);
        CompactObservedCompatibility changed = Current() with
        {
            Native = new(new string('d', 64))
        };
        store.TryRecord(selected.Value!, changed, Proof(), out var signed);
        string path = Path.Combine(store.Root, signed.Value!.Value + ".json");
        string original = System.IO.File.ReadAllText(path);
        JsonObject forged = JsonNode.Parse(original)!.AsObject();
        forged["payload"]!["compatibility"]!["implementations"]!["native"] = new string('e', 64);
        System.IO.File.WriteAllText(path, forged.ToJsonString());
        Require(!store.TryRead(signed.Value, selected.Value!, changed, out var tampered)
            && tampered.Error == CompactAcceptanceError.ForgedEvidence, "payload tampering refused");
        System.IO.File.WriteAllText(path, original);
        CompactAcceptanceStore.TryOpen(store.Root, Exports(root), Authority with
        {
            Connection = "foreign"
        }, out var foreign);
        Require(!foreign.Value!.TryRead(signed.Value, selected.Value!, changed, out var wrongAuthority)
            && wrongAuthority.Error == CompactAcceptanceError.ForeignOrStaleEvidence, "same key does not bypass authority binding");
        Require(!store.TryRead(signed.Value, selected.Value!, changed with
        {
            Gateway = new(new string('f', 64))
        }, out var scope)
            && scope.Error == CompactAcceptanceError.ScopeChanged, "receipt must match current implementation");
        var revalidate = new CompactRevalidation(library, store);
        Require(!revalidate.TryAccept("editor.write", 1, signed.Value, "other-node", changed, out var wrongNode)
            && wrongNode.Error == CompactAcceptanceError.AuthorityChanged, "node authority checked before save");
        Require(!revalidate.TryAccept("editor.write", 1, signed.Value, Authority.Node,
            changed with
            {
                Application = new("other-variant", "1.0")
            }, out var incompatible)
            && incompatible.Error == CompactAcceptanceError.IncompatibleScope, "receipt cannot widen application scope");
        Require(library.Revision == 1, "all rejected receipts leave revision unchanged");
    }

    private static void InvalidProofs(string root)
    {
        var library = Library(root);
        var store = Evidence(root);
        library.TryGet("editor.write", out var function);
        CompactAcceptanceProof proof = Proof();
        Require(!store.TryRecord(function.Value!, Current(), proof with
        {
            Test = CompactAcceptanceTest.ReadOnly
        }, out var readOnly)
            && readOnly.Error == CompactAcceptanceError.InvalidProof, "read-only proof cannot approve a writer");
        Require(!store.TryRecord(function.Value!, Current(), proof with
        {
            Verified = false
        }, out _), "unverified oracle rejected");
        Require(!store.TryRecord(function.Value!, Current(), proof with
        {
            Oracle = new("bad")
        }, out _), "missing oracle digest rejected");
        Require(!store.TryRecord(function.Value!, Current(), proof with
        {
            Execution = new("bad")
        }, out _),
            "missing execution digest rejected");
        Require(!store.TryRecord(function.Value!, Current(), proof with
        {
            Isolation = proof.Isolation with
            {
                Node = "foreign"
            }
        }, out _),
            "isolation node matches authority");
        Require(!store.TryRecord(function.Value!, Current() with
        {
            Native = null
        }, proof, out var missing)
            && missing.Error == CompactAcceptanceError.CaptureEvidenceMissing, "unknown implementation cannot get receipt");
        Require(Directory.GetFiles(store.Root, "*.json").Length == 0, "failed proofs create no receipt file");
    }

    private static void PrivateFiles(string root)
    {
        var library = Library(root);
        var store = Evidence(root);
        library.TryGet("editor.write", out var function);
        store.TryRecord(function.Value!, Current(), Proof(), out var receipt);
        string file = Path.Combine(store.Root, receipt.Value!.Value + ".json");
        string original = System.IO.File.ReadAllText(file);
        Require(!CompactAcceptanceStore.TryOpen(Path.Combine(root, "absent"), Exports(root), Authority, out _),
            "reader does not initialize signing keys");
        if (!OperatingSystem.IsWindows())
        {
            System.IO.File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Require(!store.TryRead(receipt.Value, function.Value!, Current(), out var publicFile)
                && publicFile.Error == CompactAcceptanceError.UnsafeEvidence, "public-readable receipt refused");
            System.IO.File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        string target = Path.Combine(root, "receipt-target.json");
        System.IO.File.WriteAllText(target, original);
        System.IO.File.Delete(file);
        System.IO.File.CreateSymbolicLink(file, target);
        Require(!store.TryRead(receipt.Value, function.Value!, Current(), out _), "symlink receipt refused");
        Require(System.IO.File.ReadAllText(target) == original, "symlink target untouched");
        Require(!CompactAcceptanceStore.TryOpen(Exports(root), Exports(root), Authority, out _), "private evidence cannot be exported");
    }

    private static CompactFunctionLibrary Library(string root)
    {
        Require(CompactFunctionLibrary.TryOpen(FunctionFile(root), Path.Combine(root, "revisions"), Application, out var library),
            "open fixture library");
        var selector = new CompactStoredSelector(CompactRole.Parse("Edit"), "Text", "text", CompactWindowKind.Main, []);
        var draft = new CompactFunctionDraft("editor.write", new(CompactTopicCategory.Use, "Editor"),
            [new(CompactRecipeAction.Write, selector, CompactRecipeRoute.NativeText, "text")],
            [new("value", selector, CompactOutputField.Value, CompactOutputCheckKind.EqualsParameter, "text")],
            CompactCaptureValidation.DemonstratedMutation);
        CompactCompatibility.TryCapture(draft, Current(), out var compatible);
        Require(library.Value!.TryInstall(draft, compatible.Value!, out _), "install fixture function");

        return library.Value;
    }

    private static CompactAcceptanceStore Evidence(string root)
    {
        Require(CompactAcceptanceStore.TryCreateForIsolatedRunner(Path.Combine(root, "receipts"), Exports(root), Authority, out var opened),
            "explicit isolated runner creates fixture key");

        return opened.Value!;
    }

    private static CompactAcceptanceProof Proof()
    {
        return new(CompactAcceptanceTest.Functional, new(CompactAcceptanceIsolationKind.PrivateVirtualDesktop, Authority.Node),
            true, new(new string('e', 64)), new(new string('f', 64)));
    }

    private static CompactObservedCompatibility Current()
    {
        var actions = new CompactActionSchemas(new Dictionary<Tool, CompactContractDigest>
        {
            [Tool.GetMachineCapabilities] = new(new string('a', 64)),
            [Tool.ListWindows] = new(new string('a', 64)),
            [Tool.GetUiTree] = new(new string('a', 64)),
            [Tool.TypeText] = new(new string('a', 64))
        }, new Dictionary<string, CompactContractDigest>());

        return new("1.0.1", CompactCompatibilityWire.Wire("2025-06-18"), "fixture-platform", Application, actions,
            new(new string('b', 64)), new(new string('c', 64)));
    }

    private static string Exports(string root)
    {
        return Path.Combine(root, "exports");
    }

    private static string FunctionFile(string root)
    {
        return Path.Combine(Exports(root), "programs", Application.Key, Application.Build, "generated-ui.json");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Signed acceptance regression failed: " + name);
        }
    }
}
