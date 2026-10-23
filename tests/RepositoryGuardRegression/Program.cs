using System.Diagnostics;
using System.Text;
using RepositoryGuard;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
var assets = new Dictionary<string, string>();
IReadOnlyList<Finding> Inspect(string path, string text)
    => RepositoryGuard.Program.Inspect(path, Encoding.UTF8.GetBytes(text), null, assets);
string token = string.Concat("gh", "p_", new string('Z', 36));
Check(Inspect("source.cs", "class Safe { }").Count == 0, "Valid code blocked.");
Check(Inspect("README.md", "Public documentation").Count == 0, "Safe documentation rejected as non-code.");
Check(Inspect("source.cs", "// " + token).Any(f => f.Rule == "github-token"), "Secret in valid source code missed.");
Check(Inspect("source.cs", "const string Password = \"hunter2\";").Any(f => f.Rule == "literal-password"), "Weak password literal missed.");
Check(Inspect("config.example.json", "{\"api_key\":\"<OPERATOR_CREDENTIAL>\"}").Count == 0, "Explicit placeholder rejected.");
foreach (string domain in new[] { "example.com", "EXAMPLE.ORG", "mail.example.net", "example.invalid", "lab.test", "sender.example" })
{
    Check(Inspect("config.example.json", "{\"sender\":\"operator@" + domain + "\"}").Count == 0,
        "Reserved example address rejected.");
}
foreach (string domain in new[] { "company.tld", "notexample.com", "example.com.company.tld" })
{
    var findings = Inspect("config.example.json", "{\"sender\":\"operator@" + domain + "\"}");
    Check(findings.Any(finding => finding.Rule == "personal-address-in-example"), "Real-looking example contact accepted.");
    Check(findings.All(finding => !finding.Path.Contains(domain)), "Example address leaked in the diagnostic.");
}
Check(Inspect("SECURITY.md", "Public contact: support@kalanis.de").Count == 0, "Intentional public contact blocked.");
foreach (string path in new[]
{
    "config.local.json", "nested/preferences.local.yml", "settings.local.yaml", ".mcp.json", "secrets/data.json",
    "captures/data.json", "screenshots/data.json", "recordings/data.json", "exports/mail.json",
    "events.jsonl", "mail.eml", "mail.msg", "archive.mbox", "archive.pst", "archive.ost", "data.sqlite-wal"
})
{
    Check(Inspect(path, "harmless").Any(finding => finding.Rule == "private-runtime-or-artifact-path"),
        "Private operator data accepted: " + path);
}
Check(Inspect("tests/fixtures/policy.json.txt", "{\"nodeId\":\"fixture\"}").Count == 0, "Reviewed text fixture rejected.");
Check(Inspect("tests/fixtures/audit.jsonl.txt", "{\"sequence\":1}").Count == 0, "Reviewed journal fixture rejected.");
Check(Inspect(".env", "harmless").Any(f => f.Rule == "private-runtime-or-artifact-path"), "Forced .env path accepted.");
Check(Inspect(".artifacts/report.md", "harmless").Any(f => f.Rule == "private-runtime-or-artifact-path"), "Private artifact accepted.");
foreach (string path in new[] { ".internal/note.md", "nested/.internal/note.md", "nested/.ARTIFACTS/mail.json" })
{
    Check(Inspect(path, "harmless").Any(f => f.Rule == "private-runtime-or-artifact-path"), "Private directory accepted: " + path);
}
Check(Inspect("credential", "harmless").Any(f => f.Rule == "private-runtime-or-artifact-path"), "Runtime credential accepted.");
foreach (string path in new[] { "paired-master.json", "paired-devices.json", "pairing-options.json", "master.lock", "node.credential" })
{
    Check(Inspect(path, "harmless").Any(f => f.Rule == "private-runtime-or-artifact-path"), "New pairing runtime state accepted: " + path);
}
string deviceCredential = new('q', 44);
Check(Inspect("renamed.json", "{\"credential\":\"" + deviceCredential + "\"}").Any(f => f.Rule == "literal-password"),
    "Renaming the pairing config bypasses credential detection.");
foreach (string path in new[]
{
    "deployment-invite.json",
    "update-plan.json",
    "update-manifest.json",
    "node-ready.json",
    "last-update.json"
})
{
    Check(Inspect(path, "harmless").Any(f => f.Rule == "private-runtime-or-artifact-path"), "Managed runtime state accepted: " + path);
}
Check(Inspect("renamed-invite.json", "{\"token\":\"" + deviceCredential + "\"}").Any(f => f.Rule == "literal-password"),
    "Renaming the invitation bypasses enrollment-token detection.");
Check(Inspect("unknown.dat", "harmless").Any(f => f.Rule == "unapproved-file-format"), "Unknown type silently skipped.");
const string browserSource = "modules/efficiency-benchmark/browser-workbench/index.html";
Check(Inspect(browserSource, "<!doctype html><title>Synthetic fixture</title>").Count == 0,
    "Reviewed browser fixture source is blocked.");
Check(Inspect(browserSource, "<!-- " + token + " -->").Any(f => f.Rule == "github-token"),
    "Reviewed browser source bypasses content scanning.");
Check(Inspect("mail.html", "harmless").Any(f => f.Rule == "unapproved-file-format"),
    "Unreviewed HTML is implicitly trusted.");
Check(Inspect(".artifacts/index.html", "harmless").Any(f => f.Rule == "private-runtime-or-artifact-path"),
    "Browser source approval allows private artifacts.");
Check(Inspect("scripts/install-windows-node.ps1", "Write-Host 'Node setup'").Count == 0, "Reviewed Windows setup text is blocked.");
Check(
    Inspect("scripts/install-windows-node.ps1", "# " + token).Any(f => f.Rule == "github-token"),
    "Reviewed setup path bypasses secret detection.");
Check(
    Inspect("scripts/unreviewed.ps1", "Write-Host 'Unknown'").Any(f => f.Rule == "unapproved-file-format"),
    "Unreviewed PowerShell format is implicitly trusted.");
Check(Inspect("scripts/render-readme-benchmark.py", "print('Synthetic measurements')").Count == 0,
    "Reviewed chart source is blocked.");
Check(Inspect("scripts/render-readme-benchmark.py", "# " + token).Any(f => f.Rule == "github-token"),
    "Reviewed chart source bypasses secret detection.");
foreach (string source in new[]
{
    "docker/performance/capabilities.py",
    "scripts/performance/adapters.py",
    "scripts/performance/contracts.py",
    "scripts/performance/coordinator.py",
    "scripts/performance/docker_lab.py",
    "scripts/performance/mail_service.py",
    "scripts/performance/main.py",
    "scripts/performance/oracle.py",
    "scripts/performance/snapshot.py",
    "scripts/performance/telemetry.py",
    "scripts/performance/workloads.py",
    "tests/PerformanceRegression/test_harness.py",
    "tests/PerformanceRegression/test_live_worker.py",
    "scripts/performance/remote_lab.py",
    "scripts/performance/chat_usage.py",
    "tests/PerformanceRegression/test_remote_control.py",
    "scripts/performance/runtime_lab.py",
    "scripts/performance/runtime_mcp.py",
    "tests/PerformanceRegression/test_native_runtime.py"
})
{
    Check(Inspect(source, "print('Synthetic desktop probe')").Count == 0, "Reviewed desktop source is blocked: " + source);
    Check(Inspect(source, "# " + token).Any(f => f.Rule == "github-token"), "Reviewed source bypasses scanning: " + source);
}
Check(Inspect("scripts/performance/unreviewed.py", "print('Unknown')").Any(f => f.Rule == "unapproved-file-format"),
    "A neighboring Python file is implicitly trusted.");
Check(Inspect("scripts/unreviewed.py", "print('Unknown')").Any(f => f.Rule == "unapproved-file-format"),
    "Unreviewed Python format is implicitly trusted.");
foreach (string examplePath in new[]
{
    "examples/agent-authoring/mail-to-tryton/build.py",
    "examples/agent-authoring/mail-to-tryton/test_build.py"
})
{
    Check(Inspect(examplePath, "print('Synthetic example')\n").Count == 0, "Reviewed authoring example rejected.");
    Check(Inspect(examplePath, "# " + token).Any(f => f.Rule == "github-token"), "Example bypassed secret inspection.");
}
Check(Inspect("examples/agent-authoring/mail-to-tryton/unreviewed.py", "print('Unknown')")
    .Any(f => f.Rule == "unapproved-file-format"), "Other example scripts were implicitly approved.");
Check(
    RepositoryGuard.Program.Inspect("source.cs", [0xff, 0xfe, 0, 1], null, assets).Any(f => f.Rule == "non-utf8-unscanned-content"),
    "Non-UTF8 skipped.");
Check(Inspect("source.cs", "safe\0hidden").Any(f => f.Rule == "binary-unscanned-content"), "NUL hidden content skipped.");
Check(Inspect("README.md", "/Users/" + "personal-name/config.json").Any(f => f.Rule == "personal-machine-path"), "Personal path leaked.");
Check(Inspect("README.md", "192." + "168.0.10").Any(f => f.Rule == "private-network-literal"), "Private infrastructure leaked.");
Check(Inspect("README.md", "subnet 192." + "168.7").Any(f => f.Rule == "private-network-literal"), "A private subnet prefix leaked.");
foreach (string address in new[] { "someone" + "@company.tld", "Person.Name" + "@mail.provider.de" })
{
    var findings = Inspect("docs/notes.md", "Contact " + address);
    Check(findings.Any(f => f.Rule == "unapproved-email-address") && findings.All(f => !f.Path.Contains(address)),
        "A foreign address was published or leaked in the diagnostic: " + address);
}
Check(Inspect("docs/notes.md", "SUPPORT@kalanis.de, alerts@offers.example, operator@lab.test").Count == 0,
    "An approved address was blocked.");
Check(
    RepositoryGuard.Program.Inspect("<commit-message>", Encoding.UTF8.GetBytes("Fix\n\nCo-Authored-By: Tool <noreply@anthropic.com>"), null,
        assets, true).Count == 0,
    "The published co-author trailer blocked a commit message.");
Check(
    RepositoryGuard.Program.Inspect("<commit-metadata>", Encoding.UTF8.GetBytes("Fix\nName\nsomeone" + "@company.tld"), null, assets, true)
        .Any(f => f.Rule == "unapproved-email-address"),
    "A foreign author address passed the commit metadata scan.");
string[] terms = ["lab-host", "Desk.PC"];
IReadOnlyList<Finding> InspectPrivate(string path, string text)
    => RepositoryGuard.Program.Inspect(path, Encoding.UTF8.GetBytes(text), null, assets, privateTerms: terms);
foreach (string text in new[] { "node LAB-HOST is up", "ssh lab-host", "host=desk.pc;" })
{
    Check(InspectPrivate("tests/case.cs", text).Any(f => f.Rule == "private-machine-or-network-name"), "A private name passed: " + text);
}
Check(InspectPrivate("tests/case.cs", "lab-hostname deskXpc office-pc").Count == 0, "A private term matched inside another word.");
var termPath = InspectPrivate("docs/lab-host/setup.md", "safe");
Check(termPath.Any(f => f.Rule == "private-machine-or-network-name") && termPath.All(f => !f.Path.Contains("lab-host")),
    "A private name in a path passed or leaked.");
Check(
    Inspect("README.md", "https://user:" + "credential@example.invalid").Any(f => f.Rule == "inline-url-credential"),
    "Inline credential URL missed.");
var nameFinding = Inspect(token + ".cs", "safe");
Check(
    nameFinding.Any(f => f.Rule == "credential-in-filename") && nameFinding.All(f => !f.Path.Contains(token)),
    "Credential filename was exposed.");
string root = Path.Combine(Path.GetTempPath(), "k-lani-bot-guard-" + Guid.CreateVersion7());
Directory.CreateDirectory(root);
try
{
    Directory.CreateDirectory(Path.Combine(root, ".release"));
    File.WriteAllText(Path.Combine(root, ".release", "approved-assets.json"), "{\"version\":1,\"assets\":{}}");
    void WriteSizeLimits(long history, long file)
        => File.WriteAllText(Path.Combine(root, ".release", "repository-size.json"),
            "{\"version\":1,\"maximumHistoryCompressedBytes\":" + history + ",\"maximumFileBytes\":" + file + "}");
    WriteSizeLimits(10485760, 1048576);
    CommandRun Run(string executable, string[] arguments, string? input = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input != null)
            process.StandardInput.Write(input);
        process.StandardInput.Close();
        process.WaitForExit();
        error.GetAwaiter().GetResult();
        return new CommandRun(process.ExitCode, output.GetAwaiter().GetResult());
    }
    string Git(params string[] arguments)
    {
        var result = Run("git", arguments);
        if (result.Exit != 0)
            throw new Exception("Fixture Git failed.");
        return result.Output.Trim();
    }
    CommandRun Guard(string mode, string? input = null)
        => Run("dotnet", [typeof(RepositoryGuard.Program).Assembly.Location, "--repo", root, "--mode", mode], input);
    Git("init", "-b", "main");
    Git("config", "user.name", "guard-fixture");
    Git("config", "user.email", "test@example.invalid");
    File.WriteAllText(Path.Combine(root, "safe.cs"), "class Safe {}");
    Git("add", "safe.cs");
    Check(Guard("index").Exit == 0, "Safe real index blocked.");
    Git("commit", "-m", "safe initial fixture");
    File.WriteAllText(Path.Combine(root, "secret.cs"), "// " + token);
    Git("add", "secret.cs");
    File.WriteAllText(Path.Combine(root, "secret.cs"), "// safe working tree");
    var staged = Guard("index");
    Check(staged.Exit == 1, "Guard scanned worktree instead of staged bytes.");
    Check(!staged.Output.Contains(token), "Secret printed in findings.");
    Git("commit", "-m", "synthetic credential fixture");
    Git("rm", "--force", "secret.cs");
    Git("commit", "-m", "delete credential fixture");
    string head = Git("rev-parse", "HEAD");
    var pushed = Guard("pre-push", $"refs/heads/main {head} refs/heads/main {new string('0', 40)}\n");
    Check(pushed.Exit == 1, "Historical deleted secret allowed in initial push.");
    Check(!pushed.Output.Contains(token), "History finding leaked secret.");
    Check(Guard("pre-push", "malformed frame\n").Exit == 2, "Malformed push failed open.");
    Git("tag", "-a", "synthetic-secret-tag", "-m", token);
    string tag = Git("rev-parse", "refs/tags/synthetic-secret-tag");
    var tagged = Guard("pre-push", $"refs/tags/synthetic-secret-tag {tag} refs/tags/synthetic-secret-tag {new string('0', 40)}\n");
    Check(tagged.Exit == 1 && !tagged.Output.Contains(token), "Annotated tag credential bypassed inspection or redaction.");
    Git("checkout", "--orphan", "clean");
    Git("rm", "--cached", "safe.cs");
    File.WriteAllText(Path.Combine(root, ".env"), "safe");
    Git("add", "--force", ".env");
    Check(Guard("index").Exit == 1, "Force-added sensitive file accepted.");
    Git("rm", "--cached", ".env");
    File.WriteAllText(Path.Combine(root, ".gitignore"), ".internal/\n.artifacts/\n");
    foreach (string privateDirectory in new[] { ".internal", ".artifacts" })
    {
        Directory.CreateDirectory(Path.Combine(root, privateDirectory));
        File.WriteAllText(Path.Combine(root, privateDirectory, "note.md"), "private note fixture");
        string privatePath = privateDirectory + "/note.md";
        Git("add", "--force", privatePath);
        Check(Guard("index").Exit == 1, "Force-added private file accepted: " + privateDirectory);
        Git("rm", "--cached", privatePath);
    }
    if (!OperatingSystem.IsWindows())
    {
        File.CreateSymbolicLink(Path.Combine(root, "link.cs"), "safe.cs");
        Git("add", "link.cs");
        Check(Guard("index").Exit == 1, "Indexed symlink accepted.");
    }
    Git("rm", "--cached", "--ignore-unmatch", "link.cs");
    File.WriteAllText(Path.Combine(root, "large.cs"), "// " + new string('x', 3000));
    Git("add", "large.cs");
    WriteSizeLimits(10485760, 2000);
    var largeFile = Guard("index");
    Check(largeFile.Exit == 1 && largeFile.Output.Contains("file-over-size-limit"), "A file above the size limit was staged.");
    WriteSizeLimits(10485760, 1048576);
    Check(Guard("index").Exit == 0, "A file within the size limit was blocked.");
    Git("commit", "-m", "large but allowed fixture");
    var withinHistory = Guard("history");
    Check(!withinHistory.Output.Contains("history-over-size-limit") && withinHistory.Output.Contains("\"history_compressed_bytes\":"),
        "A small history was reported over its limit or without its size.");
    WriteSizeLimits(10, 1048576);
    Check(Guard("history").Output.Contains("history-over-size-limit"), "A history above its compressed limit was not reported.");
    string sized = Git("rev-parse", "HEAD");
    var sizedPush = Guard("pre-push", $"refs/heads/clean {sized} refs/heads/clean {new string('0', 40)}\n");
    Check(sizedPush.Exit == 1 && sizedPush.Output.Contains("history-over-size-limit"), "A push above the history limit was allowed.");
    File.Delete(Path.Combine(root, ".release", "repository-size.json"));
    Check(Guard("index").Exit == 2, "A missing size policy did not block publication.");
    WriteSizeLimits(10485760, 1048576);
    File.WriteAllText(Path.Combine(root, ".git", "info", PublicationIdentityPolicy.TermFileName), "# lab\n\nlab-host\n");
    File.WriteAllText(Path.Combine(root, "node.cs"), "// runs on Lab-Host");
    Git("add", "node.cs");
    var privateName = Guard("index");
    Check(privateName.Exit == 1 && privateName.Output.Contains("private-machine-or-network-name")
            && !privateName.Output.Contains("lab-host", StringComparison.OrdinalIgnoreCase),
        "A staged private machine name from the term list was accepted or printed.");
    Git("rm", "--cached", "node.cs");
    Check(Guard("index").Exit == 0, "The private term list blocked a clean index.");
    Check(LineLengthPolicy.Inspect("a.cs", new string('x', 140)).Count == 0, "A line of exactly 140 characters was reported.");
    var longLine = LineLengthPolicy.Inspect("a.cs", "short\n" + new string('x', 141) + "\nshort");
    Check(longLine.Count == 1 && longLine[0].Line == 2 && longLine[0].Length == 141, "A line of 141 characters was not reported.");
    Check(LineLengthPolicy.Inspect("a.cs", new string('ö', 140) + "\r\n").Count == 0, "Umlauts were counted as bytes or CR was counted.");
    Directory.CreateDirectory(Path.Combine(root, "src", "obj"));
    File.WriteAllText(Path.Combine(root, "src", "ok.cs"), new string('x', 140) + "\n");
    File.WriteAllText(Path.Combine(root, "src", "obj", "generated.cs"), new string('x', 500) + "\n");
    Check(Guard("line-length").Exit == 0, "A tree with only 140-character lines, or a generated file, failed the line-length mode.");
    File.WriteAllText(Path.Combine(root, "src", "long.cs"), "ok\n" + new string('x', 141) + "\n");
    var tooLong = Guard("line-length");
    Check(tooLong.Exit == 1 && tooLong.Output.Contains("\"Line\":2"), "A 141-character line did not fail the line-length mode.");
    Console.WriteLine($"{checks} repository publication guard checks passed.");
}
finally { Directory.Delete(root, true); }

// The exit code and standard output of one fixture command.
record CommandRun(int Exit, string Output);
