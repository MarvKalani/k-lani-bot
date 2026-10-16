using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.ReleaseReview;

string root = Path.Combine(Path.GetTempPath(), "k-lani-bot-release-review-" + Guid.CreateVersion7());
string candidate = Path.Combine(root, "candidate");
Directory.CreateDirectory(candidate);
string artifact = Path.Combine(candidate, "synthetic-artifact.txt");
File.WriteAllText(artifact, "original");
string profileFile = Path.Combine(root, "profile.json");
File.WriteAllText(profileFile, new JsonObject
{
    ["format"] = 1,
    ["id"] = "core-linux-x11",
    ["product"] = "k-lani-bot",
    ["release_channel"] = "alpha",
    ["external_certification_required_by_release_gate"] = false,
    ["runtime"] = "linux-x64",
    ["community_cartridge_activation_approved"] = false,
    ["optional_model_images_included"] = false,
    ["other_platforms_approved"] = false
}.ToJsonString());
string assessmentFile = Path.Combine(root, "assessment.json");
File.WriteAllText(assessmentFile, new JsonObject
{
    ["technical_gate_passed"] = true,
    ["created_at_utc"] = DateTimeOffset.UtcNow,
    ["source_commit"] = new string('a', 40),
    ["candidate_sha256"] = ReleaseApproval.CandidateDigest(candidate),
    ["profile_sha256"] = ReleaseApproval.HashFile(profileFile)
}.ToJsonString());
using var reviewerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
string privateKey = Path.Combine(root, "private.pem");
File.WriteAllText(privateKey, reviewerKey.ExportECPrivateKeyPem());
string trustFile = Path.Combine(root, "reviewer.json");
ReleaseApproval.Write(trustFile, new TrustedReleaseReviewer("synthetic-reviewer", reviewerKey.ExportSubjectPublicKeyInfoPem()));
string approvalFile = Path.Combine(root, "approval.json");
DateTimeOffset now = DateTimeOffset.UtcNow;
var payload = new ReleaseApprovalPayload(1, "k-lani-bot", ReleaseApproval.CandidateDigest(candidate),
    ReleaseApproval.HashFile(profileFile), ReleaseApproval.HashFile(assessmentFile), new string('a', 40),
    "synthetic-reviewer", ReviewKind.MaintainerSecurityAcceptance, now, now.AddDays(1));
var signed = ReleaseApproval.Sign(payload, privateKey);
ReleaseApproval.Write(approvalFile, signed);
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition)
    {
        throw new Exception(message);
    }
    checks++;
}
void Denied(Action operation, string message)
{
    bool denied = false;
    try
    {
        operation();
    }
    catch (Exception exception) when (
        exception is UnauthorizedAccessException or InvalidDataException or CryptographicException or JsonException)
    {
        denied = true;
    }
    Check(denied, message);
}
ReleaseApprovalPayload Verify(DateTimeOffset time)
    => ReleaseApproval.Verify(candidate, profileFile, assessmentFile, approvalFile, trustFile, time);
Check(Verify(now).ReviewerId == "synthetic-reviewer", "Configured reviewer signature must verify.");
File.WriteAllText(artifact, "modified");
Denied(() => Verify(now), "Modified artifact must invalidate approval.");
File.WriteAllText(artifact, "original");
string originalProfile = File.ReadAllText(profileFile);
File.WriteAllText(profileFile, originalProfile + "\n");
Denied(() => Verify(now), "Changed profile bytes must invalidate approval.");
File.WriteAllText(profileFile, originalProfile);
string originalAssessment = File.ReadAllText(assessmentFile);
File.WriteAllText(assessmentFile, originalAssessment + "\n");
Denied(() => Verify(now), "Changed assessment bytes must invalidate approval.");
File.WriteAllText(assessmentFile, originalAssessment);
Denied(() => Verify(now.AddDays(2)), "Expired approval must fail.");
Denied(() => Verify(now.AddMinutes(-10)), "Future approval must fail.");
using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
ReleaseApproval.Write(trustFile, new TrustedReleaseReviewer("synthetic-reviewer", wrongKey.ExportSubjectPublicKeyInfoPem()));
Denied(() => Verify(now), "Untrusted key must fail even with the same reviewer label.");
ReleaseApproval.Write(trustFile, new TrustedReleaseReviewer("different-reviewer", reviewerKey.ExportSubjectPublicKeyInfoPem()));
Denied(() => Verify(now), "Different reviewer identity must fail.");
ReleaseApproval.Write(trustFile, new TrustedReleaseReviewer("synthetic-reviewer", reviewerKey.ExportSubjectPublicKeyInfoPem()));
var forged = JsonNode.Parse(File.ReadAllText(approvalFile))!.AsObject();
forged["IndependentAudit"] = true;
File.WriteAllText(approvalFile, forged.ToJsonString());
Denied(() => Verify(now), "An unknown self-certification field must not be accepted.");
File.WriteAllText(approvalFile, "{\"Payload\":{},\"Payload\":{},\"Signature\":\"invalid\"}");
Denied(() => Verify(now), "Duplicate metadata keys must fail.");
ReleaseApproval.Write(approvalFile, signed);
Denied(() => ReleaseApproval.Sign(payload with { ValidUntilUtc = now.AddDays(31) }, privateKey), "Approval lifetime must be bounded.");
string link = Path.Combine(candidate, "escape");
Directory.CreateSymbolicLink(link, root);
Denied(() => ReleaseApproval.CandidateDigest(candidate), "Directory link must not escape the candidate inventory.");
Directory.Delete(link);
Check(Verify(now).ReviewKind == ReviewKind.MaintainerSecurityAcceptance, "Restored candidate must retain its original acceptance kind.");
File.Delete(approvalFile);
ReleaseApproval.ValidateCandidate(candidate, profileFile, assessmentFile, new string('a', 40), now);
Check(true, "Passing alpha candidate does not require an external certificate or reviewer signature.");
File.WriteAllText(artifact, "changed alpha");
Denied(() => ReleaseApproval.ValidateCandidate(candidate, profileFile, assessmentFile, new string('a', 40), now),
    "Unsigned alpha must still reject changed artifact bytes.");
File.WriteAllText(artifact, "original");
Denied(() => ReleaseApproval.ValidateCandidate(candidate, profileFile, assessmentFile, new string('b', 40), now),
    "Unsigned alpha must still reject another source commit.");
Console.WriteLine($"{checks} release approval regression checks passed.");
