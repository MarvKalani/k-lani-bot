using System.Text.Json.Nodes;
using KLaniBot;

string example = Path.GetFullPath(args[0]), schema = Path.GetFullPath(args[1]);
string root = Path.Combine(Path.GetTempPath(), "k-lani-bot-cartridge-review-test-" + Guid.CreateVersion7());
Directory.CreateDirectory(root);
int passed = 0;
void Check(bool ok, string label) { if (!ok) throw new Exception(label); passed++; }
void Restore()
{
    if (Directory.Exists(root))
        Directory.Delete(root, true);
    Directory.CreateDirectory(Path.Combine(root, "recipes"));
    File.Copy(Path.Combine(example, "manifest.json"), Path.Combine(root, "manifest.json"));
    File.Copy(Path.Combine(example, "recipes", "write-fixture.json"), Path.Combine(root, "recipes", "write-fixture.json"));
}
void Edit(Action<JsonObject> change)
{
    var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")))!.AsObject();
    change(manifest);
    File.WriteAllText(Path.Combine(root, "manifest.json"), manifest.ToJsonString());
}
void Denied(Action mutation, string label)
{
    Restore();
    mutation();
    bool rejected = false;
    try
    {
        CartridgePackageReview.Inspect(root, schema);
    }
    catch (Exception ex) when (
        ex is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
    {
        rejected = true;
    }
    Check(
        rejected,
        label);
}
try
{
    Restore();
    var valid = CartridgePackageReview.Inspect(root, schema);
    Check(valid["valid_for_quarantine"]!.GetValue<bool>(), "Valid draft intake");
    Check(!valid["release_approved"]!.GetValue<bool>() && !valid["activation_allowed"]!.GetValue<bool>(), "Intake does not self-approve");
    Check(valid["malware_assessment"]!.ToString() == "not_performed", "No fake malware verdict");
    Denied(() => File.AppendAllText(Path.Combine(root, "recipes", "write-fixture.json"), " "), "Tampered recipe digest denied");
    Denied(() => File.WriteAllText(Path.Combine(root, "extra.json"), "{}"), "Undeclared file denied");
    Denied(() => Edit(m => m["approved"] = true), "Author approval flag denied");
    Denied(() => Edit(m => m["files"]![0]!["path"] = "../outside.json"), "Traversal denied");
    Denied(() => Edit(m => m["files"]![0]!["path"] = "recipes/con.json"), "Windows reserved path denied");
    Denied(() => Edit(m => m["permissions"]!["scopes"]!.AsArray().Add("shell")), "Elevated scopes denied");
    Denied(
        () => Edit(m => m["dependencies"]!.AsArray().Add(new JsonObject
        {
            ["id"] = m["id"]!.ToString(),
            ["version"] = "1.0.0",
            ["sha256"] = new string('0', 64)
        })),
        "Self dependency denied");
    Denied(() => Edit(m => m["entrypoints"]!.AsArray().Add(m["entrypoints"]![0]!.DeepClone())), "Duplicate entrypoint denied");
    Denied(() => Edit(m => m["permissions"]!["effects"] = new JsonArray()), "Undeclared effect denied");
    Denied(() => File.WriteAllText(Path.Combine(root, "manifest.json"), "{\"format\":1,\"format\":1}"), "Duplicate JSON keys denied");
    if (!OperatingSystem.IsWindows())
        Denied(
            () => File.CreateSymbolicLink(Path.Combine(root, "recipes", "linked.json"), Path.Combine(root, "manifest.json")),
            "Symlink intake denied");
    Console.WriteLine($"{passed} cartridge intake regression checks passed.");
}
finally { Directory.Delete(root, true); }
