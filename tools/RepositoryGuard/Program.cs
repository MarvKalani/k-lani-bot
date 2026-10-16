using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RepositoryGuard;

// One rule the guard found broken, with a path that is redacted when the path itself could leak a secret.
public sealed record Finding(string Rule, string Path, int Line, string? Commit);
// The commit and push hooks run this guard on the staged files, the commit message or the history to be pushed; any finding or failure
// blocks publication, and no secret value is printed.
public static class Program
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly TimeSpan RegexDeadline = TimeSpan.FromSeconds(1);
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".csproj",
        ".xaml",
        ".mjs",
        ".swift",
        ".md",
        ".json",
        ".yml",
        ".yaml",
        ".sh",
        ".xml",
        ".plist",
        ".pbxproj",
        ".xcscheme",
        ".toml",
        ".txt",
        ".props",
        ".targets",
        ".sln",
        ".editorconfig"
    };
    private static readonly HashSet<string> SpecialFiles = new(StringComparer.Ordinal)
    {
        ".gitignore",
        ".dockerignore",
        ".gitattributes",
        "Dockerfile.node",
        "Dockerfile",
        ".githooks/pre-commit",
        ".githooks/pre-push",
        ".githooks/commit-msg",
        ".github/CODEOWNERS",
        "modules/laya/requirements.lock",
        "modules/efficiency-benchmark/browser-workbench/index.html",
        "scripts/install-windows-node.ps1",
        "scripts/render-readme-benchmark.py",
        "examples/agent-authoring/mail-to-tryton/build.py",
        "examples/agent-authoring/mail-to-tryton/test_build.py",
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
    };
    // The first PathPatternCount rules also apply to file paths; keep them first.
    private sealed record SecretPattern(string Name, string Pattern);

    private static readonly SecretPattern[] Patterns = [
        new("private-key", @"-----BEGIN (?:RSA |EC |DSA |OPENSSH |ENCRYPTED )?PRIVATE KEY-----"),
        new("github-token", @"\b(?:gh[pousr]_[A-Za-z0-9]{30,255}|github_pat_[A-Za-z0-9_]{40,255})\b"),
        new("cloud-access-key", @"\b(?:AKIA|ASIA)[A-Z0-9]{16}\b"),
        new("google-api-key", @"\bAIza[A-Za-z0-9_-]{35}\b"),
        new("slack-token", @"\bxox[baprs]-[A-Za-z0-9-]{10,255}\b"),
        new("stripe-live-key", @"\b(?:sk|rk)_live_[A-Za-z0-9]{16,255}\b"),
        new("openai-style-key", @"\bsk-(?:proj-|ant-api[0-9]+-)?[A-Za-z0-9_-]{32,255}\b"),
        new("jwt-credential", @"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b"),
        new("inline-url-credential", @"(?i)\b(?:https?|postgres(?:ql)?|mysql|redis|mongodb(?:\+srv)?):\/\/[^\s/:<>]+:[^\s/@<>]+@"),
        new("personal-machine-path", @"(?i)(?:/Users/|/home/|[A-Z]:\\Users\\)(?!user(?:/|\\)|example(?:/|\\)|<|\$)[A-Za-z0-9_.-]+(?:/|\\)"),
        new(
            "private-network-literal",
            @"\b(?:192\.168\.[0-9]{1,3}\.[0-9]{1,3}|10\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}|"
            + @"172\.(?:1[6-9]|2[0-9]|3[01])\.[0-9]{1,3}\.[0-9]{1,3})\b"),
        new("private-hostname", @"(?i)\b[A-Za-z0-9_.-]+\.fritz\.box\b"),
        new(
            "literal-password",
            "(?i)(?:password|passwd|pwd|client[_-]?secret|api[_-]?key|access[_-]?token|auth[_-]?token|credential|token)"
            + "[\"'\\] ]{0,5}\\s*[:=]\\s*[\"'](?<secret>[^\"'\\r\\n]{4,256})[\"']")
    ];
    private static bool Placeholder(string value)
        => Regex.IsMatch(
            value,
            @"^(?:<[^>]+>|\$\{[^}]+\}|\$[A-Z_]+|\{[^}]+\}|REPLACE_[A-Z_]+|YOUR_[A-Z_]+|EXAMPLE_[A-Z_]+)$",
            RegexOptions.CultureInvariant,
            RegexDeadline);
    private const int MaximumScannedBytes = 4 * 1024 * 1024;
    private const int MaximumFindings = 100;
    // The first patterns are the ones that also apply to file paths.
    private const int PathPatternCount = 9;

    public static IReadOnlyList<Finding> Inspect(
        string path,
        byte[] data,
        string? commit,
        IReadOnlyDictionary<string,
        string> approvedAssets,
        bool metadata = false)
    {
        var findings = new List<Finding>();
        if (!metadata && !AddFileFindings(path, data, commit, approvedAssets, findings))
        {
            return findings;
        }

        AddContentFindings(path, data, commit, findings);
        return findings;
    }

    // Returns whether the content still has to be scanned: a pinned asset is judged by its hash alone, and an oversized
    // file is reported instead of read.
    private static bool AddFileFindings(
        string path,
        byte[] data,
        string? commit,
        IReadOnlyDictionary<string, string> approvedAssets,
        List<Finding> findings)
    {
        if (Patterns.Take(PathPatternCount).Any(rule => Regex.IsMatch(path, rule.Pattern, RegexOptions.CultureInvariant, RegexDeadline)))
            findings.Add(new("credential-in-filename", "<redacted-path>", 0, commit));
        if (path.Any(c => char.IsControl(c)) || path.Contains('\\') || path.StartsWith('/') || path.Split('/').Contains(".."))
            findings.Add(new("unsafe-path", "<redacted-path>", 0, commit));
        if (IsPrivateRuntimeOrArtifactPath(path))
            findings.Add(new("private-runtime-or-artifact-path", SafePath(path), 0, commit));
        if (approvedAssets.TryGetValue(path, out string? hash))
        {
            if (Convert.ToHexStringLower(SHA256.HashData(data)) != hash)
                findings.Add(new("unreviewed-binary-asset", SafePath(path), 0, commit));
            // Assets are reviewed by exact content hash, never classified as safe code.
            return false;
        }
        string extension = Path.GetExtension(path.ToLowerInvariant().Split('/').Last());
        if (!TextExtensions.Contains(extension) && !SpecialFiles.Contains(path) && !SpecialFiles.Contains(Path.GetFileName(path)))
            findings.Add(new("unapproved-file-format", SafePath(path), 0, commit));
        if (data.Length > MaximumScannedBytes)
        {
            findings.Add(new("oversized-unscanned-file", SafePath(path), 0, commit));
            return false;
        }
        return true;
    }

    private static bool IsPrivateRuntimeOrArtifactPath(string path)
    {
        var parts = path.ToLowerInvariant().Split('/');
        string name = parts.Last();
        string extension = Path.GetExtension(name);

        return parts.Any(p =>
                p is ".artifacts" or ".internal" or ".k-lani-bot-state" or ".ssh" or ".aws" or ".azure"
                    or "node_modules" or "bin" or "obj" or "logs" or "backups" or "secrets"
                    or "captures" or "screenshots" or "recordings" or "exports"
                || p.StartsWith("dist-", StringComparison.Ordinal))
            || name == ".env"
                || name.StartsWith(".env.", StringComparison.Ordinal)
                || name is "credential" or "credentials" or "nodes.json" or "policy.json" or "worker-policy.json" or "installation.json"
                    or "last-run.json" or "visibility-preferences.json" or "paired-master.json" or "paired-devices.json"
                    or "pairing-options.json" or "master.lock" or "deployment-invite.json" or "update-plan.json"
                    or "update-manifest.json" or "node-ready.json" or "last-update.json"
            || name.EndsWith(".credential", StringComparison.Ordinal)
            || name.EndsWith(".local.json", StringComparison.Ordinal)
            || name.EndsWith(".local.yml", StringComparison.Ordinal)
            || name.EndsWith(".local.yaml", StringComparison.Ordinal)
            || name == ".mcp.json"
            || extension is ".pem" or ".key" or ".pfx" or ".p12" or ".kdbx" or ".db" or ".sqlite" or ".sqlite3" or ".log" or ".bundle"
                or ".zip" or ".tar" or ".gz" or ".dll" or ".exe" or ".dylib" or ".so" or ".mobileconfig"
                or ".jsonl" or ".eml" or ".msg" or ".mbox" or ".pst" or ".ost"
                or ".db-wal" or ".db-shm" or ".db-journal" or ".sqlite-wal" or ".sqlite-shm" or ".sqlite3-wal" or ".sqlite3-shm";
    }

    // Content that is not UTF-8 text cannot be scanned and is reported as such; otherwise every pattern is searched.
    private static void AddContentFindings(string path, byte[] data, string? commit, List<Finding> findings)
    {
        string content;
        try
        {
            content = Utf8.GetString(data);
        }
        catch (DecoderFallbackException) { findings.Add(new("non-utf8-unscanned-content", SafePath(path), 0, commit)); return; }
        if (content.Contains('\0'))
        {
            findings.Add(new("binary-unscanned-content", SafePath(path), 0, commit));
            return;
        }

        findings.AddRange(ExamplePrivacyPolicy.Inspect(SafePath(path), content, commit));
        foreach (var (name, expression) in Patterns)
        {
            foreach (Match match in Regex.Matches(content, expression, RegexOptions.CultureInvariant, RegexDeadline))
            {
                if (name == "literal-password" && Placeholder(match.Groups["secret"].Value))
                    continue;
                int line = 1 + content.AsSpan(0, match.Index).Count('\n');
                findings.Add(new(name, SafePath(path), line, commit));
                if (findings.Count >= MaximumFindings)
                    return;
            }
        }
    }

    private static string SafePath(string path)
        => path.Any(char.IsControl)
            || Patterns.Take(PathPatternCount).Any(rule => Regex.IsMatch(path, rule.Pattern, RegexOptions.CultureInvariant, RegexDeadline))
                ? "<redacted-path>"
                : path;
    // Throws IOException when git fails.
    private static byte[] Git(string root, params string[] args)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in args)
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new IOException("Git inspection failed; publication is blocked.");
        error.GetAwaiter().GetResult();
        return output.ToArray();
    }
    private static string GitText(string root, params string[] args) => Utf8.GetString(Git(root, args));
    private static string? Option(string[] args, string key)
    {
        int i = Array.IndexOf(args, key);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // Any failure blocks publication; nothing about the cause is printed, because it could contain a secret.
    public static int Main(string[] args)
    {
        try
        {
            string root = Path.GetFullPath(Option(args, "--repo") ?? Environment.CurrentDirectory);
            string mode = Option(args, "--mode") ?? "index";
            // Style, not publication safety: it needs no Git history and no asset policy.
            if (mode == "line-length")
                return LineLengthPolicy.Run(root);
            string policyRoot = Path.GetFullPath(Option(args, "--policy-root") ?? root);
            var scan = new GuardScan(root, ReadAssetPolicy(policyRoot), RepositorySizePolicy.Read(policyRoot));
            scan.Collect(mode, args);
            scan.ScanCommits();
            if (mode is "history" or "pre-push")
                scan.CheckHistorySize();
            Console.WriteLine(scan.Report(mode));
            return scan.Findings.Count == 0 ? 0 : 1;
        }
        catch
        {
            Console.Error.WriteLine(
            "Repository inspection failed or incomplete; publication blocked. No source or secret values "
            + "printed.");
            return 2;
        }
    }

    // Throws InvalidDataException for an unversioned policy or an asset pin that is no SHA-256.
    private static Dictionary<string, string> ReadAssetPolicy(string policyRoot)
    {
        var assetPolicy = JsonNode.Parse(File.ReadAllText(Path.Combine(policyRoot, ".release", "approved-assets.json")))!;
        if (assetPolicy["version"]?.GetValue<int>() != 1)
            throw new InvalidDataException("Reviewed asset policy required.");

        var assets = assetPolicy["assets"]!.AsObject().ToDictionary(p => p.Key, p => p.Value!.ToString(), StringComparer.Ordinal);
        if (assets.Any(p => !Regex.IsMatch(p.Value, "^[0-9a-f]{64}$")))
            throw new InvalidDataException("Invalid asset pin.");
        return assets;
    }

    // One run of the guard: which blobs and commits were scanned and what was found. Every blob is read once per path.
    private sealed class GuardScan(string root, Dictionary<string, string> assets, RepositorySizePolicy sizes)
    {
        private const int MaximumBlobs = 20000;
        private const int MaximumCommits = 10000;
        private readonly HashSet<string> _checkedBlobs = new();
        private readonly HashSet<string> _commits = new();
        private int _trees;
        private bool _historySizeChecked;

        public List<Finding> Findings
        {
            get;
        } = new();

        // Throws InvalidDataException for a malformed push frame and ArgumentException for an unknown mode or a missing message.
        public void Collect(string mode, string[] args)
        {
            if (mode == "index")
                ScanEntries(GitText(root, "ls-files", "--stage", "-z"), true, null);
            else if (mode == "message")
                Findings.AddRange(
                    Inspect(
                        "<commit-message>",
                        File.ReadAllBytes(Option(args, "--message") ?? throw new ArgumentException("Commit message file required.")),
                        null,
                        assets,
                        true));
            else if (mode == "history")
                CollectHistory(Option(args, "--ref"));
            else if (mode == "pre-push")
                CollectPushedReferences();
            else
                throw new ArgumentException("Unknown guard mode.");
        }

        private void CollectHistory(string? selected)
        {
            if (selected != null)
            {
                AddRef(selected);
                return;
            }

            foreach (string commit in GitText(root, "rev-list", "--all").Split('\n', StringSplitOptions.RemoveEmptyEntries))
                _commits.Add(commit);
            string tagList = GitText(root, "for-each-ref", "--format=%(objectname)", "refs/tags");
            foreach (string tag in tagList.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                ScanTag(tag);
        }

        // Throws InvalidDataException for a push reference line that does not have four columns.
        private void CollectPushedReferences()
        {
            string? line;
            while ((line = Console.ReadLine()) != null)
            {
                var columns = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length != 4)
                    throw new InvalidDataException("Invalid push reference frame.");
                if (columns[1].All(c => c == '0'))
                    continue;
                AddRef(columns[1]); // Full ancestry: deleted historical secrets must also block a new remote.
            }
        }

        public void ScanCommits()
        {
            foreach (string commit in _commits)
            {
                ScanEntries(GitText(root, "ls-tree", "-r", "-z", commit), false, commit);
                _trees++;
                Findings.AddRange(
                    Inspect("<commit-metadata>", Git(root, "show", "-s", "--format=%B%n%an%n%ae%n%cn%n%ce", commit), commit, assets, true));
            }
        }

        // The whole history being published, each file version once; a finding names no file because the sum is the problem.
        public void CheckHistorySize()
        {
            _historySizeChecked = true;
            if (sizes.HistoryTooLarge())
                Findings.Add(new("history-over-size-limit", "<history>", 0, null));
        }

        public string Report(string mode)
        {
            return JsonSerializer.Serialize(new
            {
                success = Findings.Count == 0,
                mode,
                blobs_scanned = _checkedBlobs.Count,
                commits_scanned = _trees,
                history_compressed_bytes = _historySizeChecked ? sizes.HistoryCompressedBytes : (long?)null,
                history_limit_bytes = _historySizeChecked ? sizes.MaximumHistoryCompressedBytes : (long?)null,
                findings = Findings,
                secret_values_redacted = true,
                absolute_guarantee = false
            });
        }

        // Throws InvalidDataException for an unexpected Git entry or when the blob limit is exceeded.
        private void ScanEntries(string records, bool index, string? commit)
        {
            foreach (string record in records.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                int tab = record.IndexOf('\t');
                if (tab < 0)
                    throw new InvalidDataException("Unexpected Git entry.");
                string path = record[(tab + 1)..];
                string[] fields = record[..tab].Split(' ');
                string fileMode = fields[0], oid = index ? fields[1] : fields[2];
                if (fileMode is not ("100644" or "100755") || index && fields[2] != "0")
                {
                    Findings.Add(new("link-submodule-or-unmerged-entry", SafePath(path), 0, commit));
                    continue;
                }
                if (_checkedBlobs.Add(path + ":" + oid))
                    ScanBlob(path, oid, commit);
                if (_checkedBlobs.Count > MaximumBlobs)
                    throw new InvalidDataException("Scan limit exceeded; no files silently skipped.");
            }
        }

        private void ScanBlob(string path, string oid, string? commit)
        {
            long length = long.Parse(
                GitText(root, "cat-file", "-s", oid).Trim(),
                System.Globalization.CultureInfo.InvariantCulture);

            if (sizes.FileTooLarge(length))
                Findings.Add(new("file-over-size-limit", SafePath(path), 0, commit));
            if (length > MaximumScannedBytes)
            {
                Findings.Add(new("oversized-unscanned-file", SafePath(path), 0, commit));
                sizes.AddUnread(oid, length);
                return;
            }
            byte[] content = Git(root, "cat-file", "blob", oid);
            sizes.AddContent(oid, content);
            Findings.AddRange(Inspect(path, content, commit, assets));
        }

        private void ScanTag(string reference)
        {
            if (GitText(root, "cat-file", "-t", reference).Trim() == "tag")
                Findings.AddRange(Inspect("<tag-message>", Git(root, "cat-file", "tag", reference), null, assets, true));
        }

        // Throws InvalidDataException for a reference that is not explicit or when the commit limit is exceeded.
        private void AddRef(string reference)
        {
            if (!Regex.IsMatch(reference, @"^(?:HEAD|[0-9a-f]{40,64}|refs/[A-Za-z0-9._/-]+)$"))
                throw new InvalidDataException("Explicit Git reference required.");
            ScanTag(reference);
            string resolved = GitText(root, "rev-parse", "--verify", reference + "^{commit}").Trim();
            foreach (string commit in GitText(root, "rev-list", resolved).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                _commits.Add(commit);
            if (_commits.Count > MaximumCommits)
                throw new InvalidDataException("History scan limit exceeded; publication blocked.");
        }
    }
}
