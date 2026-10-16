using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Private discovery storage is tested with synthetic structures, including legacy import, failed writes and injected state.
internal static class DiscoveryStoreCases
{
    private static readonly CompactProgramIdentity Application = new("fixture", "1.0");
    private static readonly CompactScanCompatibility Compatibility = new("node-fixture", "platform-fixture", "1.0");
    private static readonly CompactNativeWindow Observed = new(10, "fixture", "1.0", "Form", 100, "start", CompactWindowKind.Main);
    private static readonly CompactScanOptions Options = new(10, "form", null, null, true, 2000,
        new(CompactResultMode.Legacy, null));
    private const string PrivateValue = "LIVE_SYNTHETIC_VALUE_NOT_FOR_STORAGE";

    public static void Run()
    {
        string directory = Path.Combine(Path.GetTempPath(), "k-lani-discovery-store-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            RoundTripAndPrivacy(Path.Combine(directory, "round-trip"));
            InvalidDocuments(Path.Combine(directory, "invalid"));
            ExternalChangeAndOwnership(Path.Combine(directory, "external"));
            UnsafePaths(directory);
            SessionOnly();
            SessionCapacity();
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void RoundTripAndPrivacy(string directory)
    {
        CompactObservedFormStore store = Open(directory);
        var scanner = new CompactFormScanner(store);
        Require(scanner.TryScan(Nodes(), Options, Observed, Compatibility, out var result), "initial structural save");
        Require(result.Value!.Observation.Contents!.Fields[0].Value == PrivateValue, "value returned live");
        string document = File.ReadAllText(store.FilePath);
        Require(!document.Contains(PrivateValue) && !document.Contains("\"pid\"") && !document.Contains("\"id\""),
            "no runtime values or identifiers persisted");
        Require(!document.Contains("\"value\"") && !document.Contains("snapshot"), "no value or observation authority persisted");
        var restarted = new CompactFormScanner(Open(directory));
        Require(restarted.TryRead(new("form", null), out var read) && read.Value!["fields"]!.AsArray().Count == 1,
            "restart reads structure");
        Require(!read.Value!.ToJsonString().Contains(PrivateValue) && scanner.IsPersistent, "stored reply has no input values");
        Require(!Directory.GetFiles(directory).Any(file => file.EndsWith(".tmp", StringComparison.Ordinal)),
            "atomic write cleaned temporary");
        if (!OperatingSystem.IsWindows())
        {
            Require(File.GetUnixFileMode(store.FilePath) == (UnixFileMode.UserRead | UnixFileMode.UserWrite), "owner-only file");
            Require(File.GetUnixFileMode(directory) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
                "owner-only application directory");
        }
    }

    private static void InvalidDocuments(string directory)
    {
        CompactObservedFormStore store = Open(directory);
        var scanner = new CompactFormScanner(store);
        Require(scanner.TryScan(Nodes(), Options, Observed, Compatibility, out _), "seed document");
        JsonObject original = JsonNode.Parse(File.ReadAllText(store.FilePath))!.AsObject();
        CheckInvalid(store.FilePath, original, document => document["version"] = 2, "future version");
        CheckInvalid(store.FilePath, original, document => document["format"] = "other", "unknown format");
        CheckInvalid(store.FilePath, original, document => document["application"]!["version"] = "other", "different build");
        CheckInvalid(store.FilePath, original, document => document["forms"]!["form"]!["latest"] = "bad", "unknown latest");
        CheckInvalid(store.FilePath, original, document => StoredField(document)["value"] = PrivateValue, "injected live value");
        CheckInvalid(store.FilePath, original, document => StoredField(document)["selector"]!["id"] = 42, "injected native ID");
        CheckInvalid(store.FilePath, original, document => StoredField(document)["actions"] = new JsonArray("execute_script"),
            "injected executable action");
        CheckInvalid(store.FilePath, original, document => StoredField(document)["key"] = "bad", "changed semantic digest");
        File.WriteAllText(store.FilePath, "{ incomplete");
        Require(!CompactObservedFormStore.TryOpen(directory, Application, out var malformed)
            && malformed.Error == CompactDiscoveryError.InvalidDocument, "malformed document refusal");
        Require(File.ReadAllText(store.FilePath) == "{ incomplete", "malformed file untouched");
        File.WriteAllText(store.FilePath, new string(' ', CompactObservedFormStore.MaximumDocumentBytes + 1));
        Require(!CompactObservedFormStore.TryOpen(directory, Application, out _), "bounded file read");
    }

    private static JsonObject StoredField(JsonObject document)
    {
        string latest = document["forms"]!["form"]!["latest"]!.GetValue<string>();

        return document["forms"]!["form"]!["states"]![latest]!["fields"]![0]!.AsObject();
    }

    private static void CheckInvalid(string file, JsonObject original, Action<JsonObject> edit, string name)
    {
        JsonObject document = original.DeepClone().AsObject();
        edit(document);
        string content = document.ToJsonString();
        File.WriteAllText(file, content);
        Require(!CompactObservedFormStore.TryOpen(Path.GetDirectoryName(file)!, Application, out var refused)
            && refused.Error == CompactDiscoveryError.InvalidDocument, name);
        Require(File.ReadAllText(file) == content, name + " preserves original bytes");
    }

    private static void ExternalChangeAndOwnership(string directory)
    {
        CompactObservedFormStore first = Open(directory);
        var firstScanner = new CompactFormScanner(first);
        var staleScanner = new CompactFormScanner(Open(directory));
        Require(firstScanner.TryScan(Nodes(), Options, Observed, Compatibility, out _), "one writer commits");
        string original = File.ReadAllText(first.FilePath);
        Require(!staleScanner.TryScan(Nodes(), Options, Observed, Compatibility, out var stale)
            && stale.Error == CompactDiscoveryError.StorageChanged, "stale writer cannot overwrite");
        Require(File.ReadAllText(first.FilePath) == original, "stale write leaves data intact");
        using (FileStream ownership = new(first.FilePath + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Require(!firstScanner.TryScan(Nodes(), Options, Observed, Compatibility, out var locked)
                && locked.Error == CompactDiscoveryError.StorageUnavailable, "concurrent ownership refusal");
            Require(File.ReadAllText(first.FilePath) == original, "locked write leaves data intact");
        }
        File.AppendAllText(first.FilePath, " ");
        Require(!firstScanner.TryScan(Nodes(), Options, Observed, Compatibility, out var external)
            && external.Error == CompactDiscoveryError.StorageChanged, "even formatting-only external edits require reload");
        Require(new CompactFormScanner(Open(directory)).TryScan(Nodes(), Options, Observed, Compatibility, out _),
            "fresh reload permits recording");
    }

    private static void UnsafePaths(string directory)
    {
        Require(!CompactObservedFormStore.TryOpen("relative", Application, out _), "relative root refusal");
        Require(!CompactObservedFormStore.TryOpen(Path.GetPathRoot(directory)!, Application, out _), "filesystem root refusal");
        Require(!CompactObservedFormStore.TryOpen(directory, Application with
        {
            Build = "unknown"
        }, out _), "unknown build refusal");
        string target = Path.Combine(directory, "link-target");
        string alias = Path.Combine(directory, "link-alias");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(alias, target);
        Require(!CompactObservedFormStore.TryOpen(alias, Application, out _), "symlink directory refusal");
        CompactObservedFormStore store = Open(target);
        File.WriteAllText(store.FilePath, "{}");
        string linkedFile = Path.Combine(directory, "file-alias");
        Directory.CreateDirectory(linkedFile);
        File.CreateSymbolicLink(Path.Combine(linkedFile, CompactObservedFormStore.FileName), store.FilePath);
        Require(!CompactObservedFormStore.TryOpen(linkedFile, Application, out _), "symlink file refusal");
        string lockFile = Path.Combine(target, CompactObservedFormStore.FileName + ".lock");
        File.CreateSymbolicLink(lockFile, store.FilePath);
        Require(!store.TryReplace(new Dictionary<string, CompactObservedForm>(), out _), "symlink lock refusal");
        Require(File.ReadAllText(store.FilePath) == "{}", "symlink refusal does not follow or replace target");
    }

    private static void SessionOnly()
    {
        var scanner = new CompactFormScanner();
        Require(scanner.TryScan(Nodes(), Options, Observed, Compatibility, out var result) && !scanner.IsPersistent,
            "reading without learning storage");
        Require(result.Value!.Observation.Contents!.Fields[0].Value == PrivateValue, "session scan keeps live values");
        Require(scanner.TryRead(new("form", null), out _), "volatile discovery available in session");
        Require(!new CompactFormScanner().TryRead(new("form", null), out var missing)
            && missing.Error == CompactDiscoveryError.UnknownState, "session-only discovery does not survive restart");
    }

    private static IReadOnlyList<CompactTreeNode> Nodes()
    {
        return [new(0, 1, CompactRole.Parse("Window"), "Form", "", "", true, false),
            new(2, 2, CompactRole.Parse("Edit"), "Name", "name", PrivateValue, true, false)];
    }

    private static void SessionCapacity()
    {
        var scanner = new CompactFormScanner();
        Require(scanner.TryScan(Nodes(), Options, Observed, Compatibility, out _), "small session state");
        CompactTreeNode[] oversized = Nodes().ToArray();
        oversized[1] = oversized[1] with
        {
            Name = new string('x', CompactObservedFormStore.MaximumDocumentBytes)
        };
        Require(!scanner.TryScan(oversized, Options, Observed, Compatibility, out var bounded)
            && bounded.Error == CompactDiscoveryError.DocumentBoundExceeded, "volatile structural storage is bounded too");
        Require(scanner.TryRead(new("form", null), out var preserved)
            && preserved.Value!["observed_states"]!.GetValue<int>() == 1, "failed session save leaves previous state intact");
    }

    private static CompactObservedFormStore Open(string directory)
    {
        Require(CompactObservedFormStore.TryOpen(directory, Application, out var opened), "open private directory");

        return opened.Value!;
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Discovery store regression failed: " + name);
        }
    }
}
