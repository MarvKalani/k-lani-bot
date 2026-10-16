using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Cartridges;

string source = Path.GetFullPath(args[0]);
string root = Path.Combine(Path.GetTempPath(), "k-lani-bot-cartridge-test-" + Guid.CreateVersion7());
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
void Rejected(Action action, string message)
{
    try
    {
        action();
    }
    catch (Exception ex) when (ex is InvalidDataException or UnauthorizedAccessException) { passed++; return; }
    throw new Exception(
        message);
}
try
{
    var good = CartridgeManifest.Load(source);
    Check(good.Id == "apple-macos-27-de-install", "Wrong cartridge.");
    Check(good.Sha256.Length == 64, "Missing content hash.");
    string candidate = Path.Combine(root, "candidate.json");
    var document = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
    File.WriteAllText(
        candidate,
        new JsonObject(document.Reverse().Select(p => KeyValuePair.Create(p.Key, p.Value?.DeepClone()))).ToJsonString());
    Check(CartridgeManifest.Load(candidate).Sha256 == good.Sha256, "Hash depends on JSON property order.");
    Action<JsonObject>[] unsafeMutations =
    [
        d => d["platform"] = "ios",
        d => d["macos_major"] = 26,
        d => d["locale"] = "en-US",
        d => d["bundle_id"] = "foreign.app",
        d => d["command"] = "sudo arbitrary",
        d => d["steps"]![0] = "modify_tcc_database",
        d => d["privacy_labels"]!["accessibility"] = "wrong pane"
    ];
    foreach (var mutation in unsafeMutations)
    {
        document = JsonNode.Parse(File.ReadAllText(source))!.AsObject();
        mutation(document);
        File.WriteAllText(candidate, document.ToJsonString());
        Rejected(() => CartridgeManifest.Load(candidate), "Unsafe/foreign cartridge accepted.");
    }
    string bundle = Path.Combine(root, "bundle");
    Directory.CreateDirectory(bundle);
    File.WriteAllText(Path.Combine(bundle, "payload"), "approved");
    string first = CartridgeManifest.FingerprintBundle(bundle);
    Check(first == CartridgeManifest.FingerprintBundle(bundle), "Non-deterministic bundle pin.");
    File.WriteAllText(Path.Combine(bundle, "payload"), "changed");
    Check(first != CartridgeManifest.FingerprintBundle(bundle), "Source changes ignored.");
    File.Move(Path.Combine(bundle, "payload"), Path.Combine(bundle, "renamed"));
    Check(first != CartridgeManifest.FingerprintBundle(bundle), "Path changes ignored.");
    if (!OperatingSystem.IsWindows())
    {
        File.CreateSymbolicLink(Path.Combine(bundle, "link"), Path.Combine(root, "candidate.json"));
        Rejected(() => CartridgeManifest.FingerprintBundle(bundle), "Bundle links accepted.");
        File.Delete(Path.Combine(bundle, "link"));
        Directory.CreateSymbolicLink(Path.Combine(root, "directory-link"), bundle);
        Rejected(() => CartridgeManifest.RejectLinks(Path.Combine(root, "directory-link", "renamed")), "Parent link accepted.");
        Directory.Delete(Path.Combine(root, "directory-link"));
    }
    Console.WriteLine($"{passed} cartridge contract/integrity checks passed.");
}
finally { Directory.Delete(root, true); }
