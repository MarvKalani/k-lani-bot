using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Common;
using KLaniBot.ReleaseReview;

if (args.Length != 5)
{
    Console.Error.WriteLine("Usage: subject output-prefix syft-path grype-path pinned-tools.json");
    return 2;
}

string outputPrefix = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(outputPrefix)!);
var trustedTools = JsonNode.Parse(File.ReadAllText(Path.GetFullPath(args[4])))!;
try
{
    string syft = VerifyScanner("syft", args[2]);
    string grype = VerifyScanner("grype", args[3]);
    string? subjectFilesDigest = Directory.Exists(args[0]) ? ReleaseApproval.CandidateDigest(args[0]) : null;
    string sbomFile = outputPrefix + ".sbom.json";
    string assessmentFile = outputPrefix + ".vulnerabilities.json";
    string configurationFile = outputPrefix + ".grype.yaml";
    var inventoryRun = ProcessRunner.Run(syft,
        ["scan", args[0], "-o", "syft-json=" + sbomFile],
        300000, Path.GetDirectoryName(outputPrefix));
    if (!inventoryRun.Success)
    {
        throw new InvalidDataException("SBOM collector failed: " + inventoryRun.Error);
    }
    var inventory = JsonNode.Parse(File.ReadAllText(sbomFile))!.AsObject();
    if (inventory["artifacts"] is not JsonArray components || components.Count == 0)
    {
        throw new InvalidDataException("Empty inventory is not an assessment.");
    }

    // Use our own empty suppression list; operator scanner preferences must not
    // silently remove findings from an automatic release decision.
    File.WriteAllText(configurationFile,
        "ignore: []\nfail-on-severity: critical\ndb:\n  validate-by-hash-on-start: true\n  validate-age: true\n  max-allowed-built-age: "
        + "48h\n");
    var scannerRun = ProcessRunner.Run(grype,
        ["--config", configurationFile, "sbom:" + sbomFile, "-o", "json", "--file", assessmentFile],
        300000, Path.GetDirectoryName(outputPrefix));
    if (!File.Exists(assessmentFile))
    {
        throw new InvalidDataException("Vulnerability scan produced no report: " + scannerRun.Error);
    }
    var assessment = JsonNode.Parse(File.ReadAllText(assessmentFile))!.AsObject();
    var database = assessment["descriptor"]?["db"]?["status"];
    if (database?["valid"]?.GetValue<bool>() != true
        || !DateTimeOffset.TryParse(database["built"]?.ToString(), out var databaseBuilt)
        || DateTimeOffset.UtcNow - databaseBuilt > TimeSpan.FromHours(48))
    {
        throw new InvalidDataException("Missing, invalid or stale vulnerability database.");
    }
    if (assessment["ignoredMatches"] is JsonArray suppressed && suppressed.Count > 0)
    {
        throw new InvalidDataException("Suppressed findings require independent review; the automatic release gate refuses them.");
    }

    var findings = assessment["matches"]!.AsArray();
    int blockingFindings = findings.Count(IsBlockingFinding);
    if (subjectFilesDigest != null && subjectFilesDigest != ReleaseApproval.CandidateDigest(args[0]))
    {
        throw new InvalidDataException("Release files changed during the assessment.");
    }
    bool gatePassed = scannerRun.Success && blockingFindings == 0;
    var result = new JsonObject
    {
        ["subject"] = args[0],
        ["subject_files_sha256"] = subjectFilesDigest,
        ["components"] = components.Count,
        ["findings"] = findings.Count,
        ["blocking_findings"] = blockingFindings,
        ["sbom_sha256"] = HashFile(sbomFile),
        ["assessment_sha256"] = HashFile(assessmentFile),
        ["database_built"] = databaseBuilt.ToString("O"),
        ["tools"] = trustedTools["tools"]!.DeepClone(),
        ["scan_completed"] = scannerRun.Success,
        ["release_gate_passed"] = gatePassed,
        ["zero_vulnerabilities"] = findings.Count == 0,
        ["malware_scan_performed"] = false
    };
    File.WriteAllText(outputPrefix + ".gate.json",
        result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    Console.WriteLine(result.ToJsonString());
    return gatePassed ? 0 : 3;
}
catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException or JsonException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

string VerifyScanner(string name, string file)
{
    string scanner = Path.GetFullPath(file);
    if (HashFile(scanner) != trustedTools["tools"]![name]!["sha256"]!.ToString())
    {
        throw new InvalidDataException("Scanner binary differs from the reviewed digest.");
    }
    return scanner;
}

static string HashFile(string file)
{
    using var stream = File.OpenRead(file);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}

static bool IsBlockingFinding(JsonNode? finding)
{
    var vulnerability = finding!["vulnerability"]!;
    return vulnerability["severity"]!.ToString() is "High" or "Critical" or "Unknown"
        || vulnerability["fix"]?["state"]?.ToString() == "fixed";
}
