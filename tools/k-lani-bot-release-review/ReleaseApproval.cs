using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KLaniBot.ReleaseReview;

// The kind of review an approval records; only the maintainer's security acceptance exists.
public enum ReviewKind
{
    MaintainerSecurityAcceptance
}

// What a reviewer signs: the candidate, profile and assessment digests, the source commit, the reviewer and the validity period.
public sealed record ReleaseApprovalPayload(
    int Format,
    string Product,
    string CandidateDigest,
    string ProfileDigest,
    string AssessmentDigest,
    string SourceCommit,
    string ReviewerId,
    ReviewKind ReviewKind,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ValidUntilUtc);

// An approval payload with the reviewer's signature over it.
public sealed record SignedReleaseApproval(ReleaseApprovalPayload Payload, string Signature);
// A reviewer whose public key the release tool trusts, written by the trust command.
public sealed record TrustedReleaseReviewer(string ReviewerId, string PublicKeyPem);
// One file of a release candidate with its size and SHA-256, as listed in the candidate inventory.
public sealed record ReleaseArtifact(string Path, long Bytes, string Sha256);

// The release tool lists a candidate's files, hashes them and checks a signed approval against exactly that candidate, commit and time, so
// an approval cannot be reused for other bytes.
public static class ReleaseApproval
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<ReviewKind>() }
    };

    public static string HashFile(string file)
    {
        RejectLinks(file);
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    // Throws InvalidDataException when the candidate is empty or too large.
    public static IReadOnlyList<ReleaseArtifact> Inventory(string directory)
    {
        string root = Path.GetFullPath(directory);
        var files = new List<string>();
        int entries = 0;
        CollectFiles(root, files, 0, ref entries);
        if (files.Count is < 1 or > 10000)
        {
            throw new InvalidDataException("The candidate inventory is empty or exceeds its file limit.");
        }
        return files.Order(StringComparer.Ordinal).Select(file => new ReleaseArtifact(
            Path.GetRelativePath(root, file).Replace('\\', '/'),
            new FileInfo(file).Length,
            HashFile(file))).ToArray();
    }

    public static string CandidateDigest(string directory)
    {
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(Inventory(directory), JsonOptions)));
    }

    // Throws CryptographicException for a key that is no ECDSA key of at least 256 bits.
    public static SignedReleaseApproval Sign(ReleaseApprovalPayload payload, string privateKeyFile)
    {
        ValidatePayload(payload, DateTimeOffset.UtcNow);
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(privateKeyFile));
        if (key.KeySize < 256)
        {
            throw new CryptographicException("Release review requires an ECDSA key of at least 256 bits.");
        }
        byte[] signature = key.SignData(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions), HashAlgorithmName.SHA256);
        return new SignedReleaseApproval(payload, Convert.ToBase64String(signature));
    }

    // Throws InvalidDataException without a payload, CryptographicException for a too small reviewer key and UnauthorizedAccessException
    // when reviewer, signature, candidate or assessment do not hold.
    public static ReleaseApprovalPayload Verify(
        string candidateDirectory,
        string profileFile,
        string assessmentFile,
        string approvalFile,
        string reviewerFile,
        DateTimeOffset now)
    {
        var approval = Read<SignedReleaseApproval>(approvalFile);
        var reviewer = Read<TrustedReleaseReviewer>(reviewerFile);
        ValidateProfile(profileFile);
        var payload = approval.Payload ?? throw new InvalidDataException("Approval payload is missing.");
        ValidatePayload(payload, now);
        if (payload.ReviewerId != reviewer.ReviewerId)
        {
            throw new UnauthorizedAccessException("Approval is not issued by the configured reviewer.");
        }
        using var key = ECDsa.Create();
        key.ImportFromPem(reviewer.PublicKeyPem);
        if (key.KeySize < 256)
        {
            throw new CryptographicException("Configured reviewer key is too small.");
        }
        if (!key.VerifyData(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions),
            Convert.FromBase64String(approval.Signature), HashAlgorithmName.SHA256))
        {
            throw new UnauthorizedAccessException("Release approval signature is invalid.");
        }
        if (payload.CandidateDigest != CandidateDigest(candidateDirectory)
            || payload.ProfileDigest != HashFile(profileFile)
            || payload.AssessmentDigest != HashFile(assessmentFile))
        {
            throw new UnauthorizedAccessException("Candidate, scope or assessment changed after review.");
        }
        var assessment = Read<JsonElement>(assessmentFile);
        ValidateAssessmentAge(assessment, now);
        if (!assessment.GetProperty("technical_gate_passed").GetBoolean()
            || assessment.GetProperty("candidate_sha256").GetString() != payload.CandidateDigest
            || assessment.GetProperty("profile_sha256").GetString() != payload.ProfileDigest
            || assessment.GetProperty("source_commit").GetString() != payload.SourceCommit)
        {
            throw new UnauthorizedAccessException("The signed assessment does not pass for this candidate.");
        }
        return payload;
    }

    // Throws UnauthorizedAccessException when the candidate does not match its passing assessment.
    public static void ValidateCandidate(
        string candidateDirectory,
        string profileFile,
        string assessmentFile,
        string sourceCommit,
        DateTimeOffset now)
    {
        ValidateProfile(profileFile);
        var assessment = Read<JsonElement>(assessmentFile);
        ValidateAssessmentAge(assessment, now);
        if (!assessment.GetProperty("technical_gate_passed").GetBoolean()
            || assessment.GetProperty("candidate_sha256").GetString() != CandidateDigest(candidateDirectory)
            || assessment.GetProperty("profile_sha256").GetString() != HashFile(profileFile)
            || assessment.GetProperty("source_commit").GetString() != sourceCommit)
        {
            throw new UnauthorizedAccessException("The alpha candidate does not match its passing technical assessment.");
        }
    }

    // Throws InvalidDataException for oversized or empty metadata.
    public static T Read<T>(string file)
    {
        RejectLinks(file);
        byte[] content = File.ReadAllBytes(file);
        if (content.Length > 4 * 1024 * 1024)
        {
            throw new InvalidDataException("Review metadata exceeds the size limit.");
        }
        using var document = JsonDocument.Parse(content);
        RejectDuplicateProperties(document.RootElement);
        return JsonSerializer.Deserialize<T>(content, JsonOptions)
            ?? throw new InvalidDataException("Review metadata is empty.");
    }

    // Throws UnauthorizedAccessException for an unsupported release scope.
    public static void ValidateProfile(string profileFile)
    {
        var profile = Read<JsonElement>(profileFile);
        if (profile.GetProperty("format").GetInt32() != 1
            || profile.GetProperty("id").GetString() != "core-linux-x11"
            || profile.GetProperty("product").GetString() != "k-lani-bot"
            || profile.GetProperty("release_channel").GetString() != "alpha"
            || profile.GetProperty("external_certification_required_by_release_gate").GetBoolean()
            || profile.GetProperty("runtime").GetString() != "linux-x64"
            || profile.GetProperty("community_cartridge_activation_approved").GetBoolean()
            || profile.GetProperty("optional_model_images_included").GetBoolean()
            || profile.GetProperty("other_platforms_approved").GetBoolean())
        {
            throw new UnauthorizedAccessException("Unsupported public release scope.");
        }
    }

    // Throws UnauthorizedAccessException when the assessment is missing or too old.
    public static void ValidateAssessmentAge(JsonElement assessment, DateTimeOffset now)
    {
        var created = assessment.GetProperty("created_at_utc").GetDateTimeOffset();
        if (created > now.AddMinutes(5) || now - created > TimeSpan.FromHours(48))
        {
            throw new UnauthorizedAccessException("A fresh technical assessment is required before release acceptance or publication.");
        }
    }

    public static void Write<T>(string file, T value)
    {
        RejectLinks(file);
        File.WriteAllText(file, JsonSerializer.Serialize(value, JsonOptions) + "\n");
    }

    // Throws UnauthorizedAccessException for an invalid, future or expired approval.
    private static void ValidatePayload(ReleaseApprovalPayload payload, DateTimeOffset now)
    {
        if (payload.Format != 1 || payload.Product != "k-lani-bot"
            || payload.ReviewKind != ReviewKind.MaintainerSecurityAcceptance
            || string.IsNullOrWhiteSpace(payload.ReviewerId) || payload.ReviewerId.Length > 128
            || !IsDigest(payload.CandidateDigest) || !IsDigest(payload.ProfileDigest)
            || !IsDigest(payload.AssessmentDigest)
            || string.IsNullOrWhiteSpace(payload.SourceCommit)
            || payload.SourceCommit.Length != 40 || !payload.SourceCommit.All(Uri.IsHexDigit)
            || payload.IssuedAtUtc > now.AddMinutes(5) || payload.ValidUntilUtc <= now
            || payload.ValidUntilUtc <= payload.IssuedAtUtc
            || payload.ValidUntilUtc - payload.IssuedAtUtc > TimeSpan.FromDays(30))
        {
            throw new UnauthorizedAccessException("Invalid, future or expired release approval.");
        }
    }

    private static bool IsDigest(string? digest)
    {
        return digest != null && digest.Length == 64
            && digest.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
    }

    // Throws InvalidDataException when the candidate is too deep or has too many entries or files.
    private static void CollectFiles(string directory, List<string> files, int depth, ref int entries)
    {
        RejectLinks(directory);
        if (depth > 16)
        {
            throw new InvalidDataException("Candidate directory depth exceeds the limit.");
        }
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (++entries > 10000)
            {
                throw new InvalidDataException("Candidate contains too many entries.");
            }
            RejectLinks(entry);
            if (Directory.Exists(entry))
            {
                CollectFiles(entry, files, depth + 1, ref entries);
            }
            else
            {
                files.Add(entry);
                if (files.Count > 10000)
                {
                    throw new InvalidDataException("Candidate contains too many files.");
                }
            }
        }
    }

    // Throws UnauthorizedAccessException when a part of the path is a link.
    private static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("Review files and artifacts must not use links.");
            }
        }
    }

    // Throws InvalidDataException for a duplicate property.
    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException("Duplicate review metadata property.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                RejectDuplicateProperties(child);
            }
        }
    }
}
