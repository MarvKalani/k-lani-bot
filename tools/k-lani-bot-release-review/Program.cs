using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.ReleaseReview;

try
{
    if (args.Length == 5 && args[0] == "verify-candidate")
    {
        ReleaseApproval.ValidateCandidate(args[1], args[2], args[3], args[4], DateTimeOffset.UtcNow);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            alpha_package_ready = true,
            release_channel = "alpha",
            independent_audit = false,
            authenticated_publisher = false,
            source_commit = args[4],
            candidate_sha256 = ReleaseApproval.CandidateDigest(args[1])
        }));
        return 0;
    }
    if (args.Length == 3 && args[0] == "notices")
    {
        WriteRuntimeNotices(args[1], args[2]);
        return 0;
    }
    if (args.Length == 4 && args[0] == "trust")
    {
        using var publicKey = ECDsa.Create();
        publicKey.ImportFromPem(File.ReadAllText(args[2]));
        ReleaseApproval.Write(args[3], new TrustedReleaseReviewer(args[1], publicKey.ExportSubjectPublicKeyInfoPem()));
        return 0;
    }
    if (args.Length == 6 && args[0] == "record")
    {
        string directory = Path.GetFullPath(args[1]);
        string stepId = args[2];
        if (!RequiredSteps.Contains(stepId))
        {
            throw new ArgumentException("Unknown release check.");
        }
        Directory.CreateDirectory(directory);
        ReleaseApproval.Write(Path.Combine(directory, stepId + ".step.json"), new CheckReceipt(
            stepId, int.Parse(args[3]), args[4], ReleaseApproval.HashFile(args[5]), DateTimeOffset.UtcNow));
        return 0;
    }
    if (args.Length == 5 && args[0] == "assess")
    {
        var assessment = Assess(args[1], args[2], args[3]);
        File.WriteAllText(args[4], assessment.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine(assessment.ToJsonString());
        return 0;
    }
    if (args.Length == 8 && args[0] == "approve")
    {
        string candidate = args[1];
        string profile = args[2];
        string assessmentFile = args[3];
        var assessment = ReleaseApproval.Read<JsonElement>(assessmentFile);
        ReleaseApproval.ValidateAssessmentAge(assessment, DateTimeOffset.UtcNow);
        ReleaseApproval.ValidateProfile(profile);
        string candidateDigest = ReleaseApproval.CandidateDigest(candidate);
        string profileDigest = ReleaseApproval.HashFile(profile);
        if (!assessment.GetProperty("technical_gate_passed").GetBoolean()
            || assessment.GetProperty("candidate_sha256").GetString() != candidateDigest
            || assessment.GetProperty("profile_sha256").GetString() != profileDigest)
        {
            throw new UnauthorizedAccessException("Review does not pass for the current candidate and profile.");
        }
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var payload = new ReleaseApprovalPayload(1, "k-lani-bot", candidateDigest,
            profileDigest, ReleaseApproval.HashFile(assessmentFile),
            assessment.GetProperty("source_commit").GetString()!, args[6],
            ReviewKind.MaintainerSecurityAcceptance, now, now.AddDays(int.Parse(args[7])));
        ReleaseApproval.Write(args[5], ReleaseApproval.Sign(payload, args[4]));
        Console.WriteLine("Maintainer acceptance signed. This is not an independent security certification.");
        return 0;
    }
    if (args.Length == 7 && args[0] == "verify")
    {
        var payload = ReleaseApproval.Verify(args[1], args[2], args[3], args[4], args[5], DateTimeOffset.UtcNow);
        if (payload.SourceCommit != args[6])
        {
            throw new UnauthorizedAccessException("Approval belongs to a different source commit.");
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            signature_verified = true,
            public_release_approved_by_configured_maintainer = true,
            independent_audit = false,
            reviewer_id = payload.ReviewerId,
            source_commit = payload.SourceCommit,
            candidate_sha256 = payload.CandidateDigest
        }));
        return 0;
    }
    Console.Error.WriteLine(
        "Commands: verify-candidate candidate profile assessment source-commit; notices project-assets output; trust reviewer-id "
        + "public-key output; record evidence-directory step-id exit-code commit log-file; assess candidate profile evidence-directory "
        + "output; approve candidate profile assessment private-key output reviewer-id valid-days; verify candidate profile assessment "
        + "approval trusted-reviewer source-commit");
    return 2;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
    or InvalidDataException or ArgumentException or JsonException or CryptographicException
    or InvalidOperationException or KeyNotFoundException or FormatException)
{
    Console.Error.WriteLine(exception.Message);
    return 2;
}

static void WriteRuntimeNotices(string assetsFile, string outputFile)
{
    var assets = ReleaseApproval.Read<JsonElement>(assetsFile);
    var folders = assets.GetProperty("packageFolders").EnumerateObject().Select(property => property.Name).ToArray();
    var notices = new System.Text.StringBuilder("Third-party runtime notices for this self-contained k-lani-bot build\n\n");
    int runtimePackages = 0;
    var downloads = assets.GetProperty("project").GetProperty("frameworks")
        .GetProperty("net10.0").GetProperty("downloadDependencies");
    foreach (var library in downloads.EnumerateArray())
    {
        string name = library.GetProperty("name").GetString()!;
        if (name is not ("Microsoft.NETCore.App.Runtime.linux-x64" or "Microsoft.AspNetCore.App.Runtime.linux-x64"))
        {
            continue;
        }
        string[] versions = library.GetProperty("version").GetString()!.Trim('[', ']').Split(',').Select(value => value.Trim()).ToArray();
        if (versions.Length != 2 || versions[0] != versions[1]
            || versions[0].Any(character => !char.IsAsciiDigit(character) && character != '.'))
        {
            throw new InvalidDataException("Runtime notices require one exact restored version.");
        }
        string relative = name.ToLowerInvariant() + "/" + versions[0];
        string package = folders.Select(folder => Path.GetFullPath(relative, folder)).First(Directory.Exists);
        var texts = Directory.EnumerateFiles(package).Where(file =>
            Path.GetFileName(file).StartsWith("LICENSE", StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(file).Equals("THIRD-PARTY-NOTICES.TXT", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (texts.Length < 2)
        {
            throw new InvalidDataException("Runtime package lacks its license or third-party notices.");
        }
        foreach (string text in texts.Order(StringComparer.Ordinal))
        {
            ReleaseApproval.HashFile(text);
            notices.AppendLine(name + "/" + versions[0] + " / " + Path.GetFileName(text));
            notices.AppendLine(File.ReadAllText(text));
            notices.AppendLine();
        }
        runtimePackages++;
    }
    if (runtimePackages != 2)
    {
        throw new InvalidDataException("Expected exact .NET and ASP.NET Core runtime notice packages for the Linux profile.");
    }
    File.WriteAllText(outputFile, notices.ToString());
}

static JsonObject Assess(string candidateDirectory, string profileFile, string evidenceDirectory)
{
    ReleaseApproval.ValidateProfile(profileFile);
    var inventory = ReleaseApproval.Inventory(candidateDirectory);
    foreach (string file in new[] { "k-lani-bot-node", "LICENSE.md", "OPERATOR-SAFETY.md", "THIRD-PARTY-NOTICES.txt" })
    {
        if (!inventory.Any(artifact => artifact.Path == file && artifact.Bytes > 0))
        {
            throw new InvalidDataException("Candidate is missing a required executable or legal notice: " + file);
        }
    }
    if (inventory.Any(artifact => artifact.Path.Contains("laya", StringComparison.OrdinalIgnoreCase)
        || artifact.Path.EndsWith(".whl", StringComparison.OrdinalIgnoreCase)))
    {
        throw new UnauthorizedAccessException("Model runtimes do not belong to the core release profile.");
    }
    string? sourceCommit = null;
    var checks = new JsonArray();
    foreach (string stepId in RequiredSteps.Values)
    {
        var receipt = ReleaseApproval.Read<CheckReceipt>(Path.Combine(evidenceDirectory, stepId + ".step.json"));
        string log = Path.Combine(evidenceDirectory, stepId + ".log");
        if (receipt.StepId != stepId || receipt.ExitCode != 0
            || receipt.LogSha256 != ReleaseApproval.HashFile(log)
            || receipt.ObservedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5)
            || DateTimeOffset.UtcNow - receipt.ObservedAtUtc > TimeSpan.FromHours(48))
        {
            throw new UnauthorizedAccessException("Missing, failed, changed or stale release check: " + stepId);
        }
        sourceCommit ??= receipt.SourceCommit;
        if (receipt.SourceCommit != sourceCommit || sourceCommit.Length != 40 || !sourceCommit.All(Uri.IsHexDigit))
        {
            throw new UnauthorizedAccessException("Release checks do not identify one source commit.");
        }
        checks.Add(JsonSerializer.SerializeToNode(receipt));
    }
    foreach (string prefix in new[] { "binary", "container" })
    {
        var gate = ReleaseApproval.Read<JsonElement>(Path.Combine(evidenceDirectory, prefix + "-security.gate.json"));
        if (!gate.GetProperty("scan_completed").GetBoolean()
            || !gate.GetProperty("release_gate_passed").GetBoolean()
            || gate.GetProperty("blocking_findings").GetInt32() != 0
            || gate.GetProperty("sbom_sha256").GetString() != ReleaseApproval.HashFile(
                Path.Combine(evidenceDirectory, prefix + "-security.sbom.json"))
            || gate.GetProperty("assessment_sha256").GetString() != ReleaseApproval.HashFile(
                Path.Combine(evidenceDirectory, prefix + "-security.vulnerabilities.json"))
            || DateTimeOffset.UtcNow - gate.GetProperty("database_built").GetDateTimeOffset() > TimeSpan.FromHours(48))
        {
            throw new UnauthorizedAccessException("Incomplete, failed or stale supply-chain assessment.");
        }
        if (prefix == "binary"
            && gate.GetProperty("subject_files_sha256").GetString() != ReleaseApproval.CandidateDigest(candidateDirectory))
        {
            throw new UnauthorizedAccessException("The binary assessment belongs to different artifact bytes.");
        }
        if (prefix == "container"
            && gate.GetProperty("subject")
                .GetString() != File.ReadAllText(Path.Combine(evidenceDirectory, "container-image-id.txt"))
                .Trim())
        {
            throw new UnauthorizedAccessException("The container assessment belongs to a different image.");
        }
    }
    // The caller receives a review packet, never an automatic self-certificate.
    // A separately configured maintainer key must approve these exact bytes.
    return new JsonObject
    {
        ["format"] = 1,
        ["product"] = "k-lani-bot",
        ["profile"] = "core-linux-x11",
        ["release_channel"] = "alpha",
        ["source_commit"] = sourceCommit,
        ["candidate_sha256"] = ReleaseApproval.CandidateDigest(candidateDirectory),
        ["profile_sha256"] = ReleaseApproval.HashFile(profileFile),
        ["created_at_utc"] = DateTimeOffset.UtcNow,
        ["technical_gate_passed"] = true,
        ["alpha_package_ready"] = true,
        ["stable_release_approved"] = false,
        ["state"] = "alpha_ready_with_declared_limits",
        ["external_certification_required_by_release_gate"] = false,
        ["independent_audit"] = false,
        ["administration_is_intended"] = true,
        ["community_cartridge_activation_approved"] = false,
        ["optional_model_images_included"] = false,
        ["checks"] = checks,
        ["artifacts"] = JsonSerializer.SerializeToNode(inventory),
        ["limits"] = new JsonArray(
            "Receipts record local test execution; they are not independent attestations.",
            "Scope is the core Linux/X11 profile only; other platforms are not approved.",
            "Intentional authorized changes and irreversible administration are supported.",
            "No guarantee against all damage, model error or same-UID/root compromise.",
            "Community activation, model images and public cloud integrations require separate review.")
    };
}

// Proof that one required release check ran: its step, exit code, source commit, log hash and time.
file sealed record CheckReceipt(string StepId, int ExitCode, string SourceCommit, string LogSha256, DateTimeOffset ObservedAtUtc);
// Every check a release candidate must have a passing receipt for before it can be packaged.
file static class RequiredSteps
{
    public static readonly string[] Values =
    [
        "format", "build", "regression", "repository-guard", "gitleaks",
        "binary-transport", "binary-sandbox", "binary-supply-chain", "container-supply-chain"
    ];
    public static bool Contains(string value) => Values.Contains(value, StringComparer.Ordinal);
}
