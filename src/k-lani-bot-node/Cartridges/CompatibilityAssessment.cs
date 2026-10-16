using System.Text.Json;
using KLaniBot.Fleet;
using KLaniBot.Inventory;

namespace KLaniBot.Cartridges;

// What the catalogue says about one exact application version on one target. Without an entry the verdict is
// Unknown, and only Compatible clears an application.
public enum CompatibilityVerdict
{
    Unknown,
    Compatible,
    Conditional,
    Incompatible,
    RequiresVm
}

// Where a reviewed assessment ended. Only Approved means the person at the node accepted a report that still
// matched the node's inventory and the catalogue at the time of the answer.
public enum ServiceAssessmentState
{
    Blocked,
    AwaitingLocalReview,
    Approved,
    Rejected,
    Invalidated,
    Expired
}

// The system an operator wants to move to, and which inventory sources must be complete to judge it.
public sealed record CompatibilityTarget(string Id, string Description, string OsFamily, string OsVersion, string Architecture,
    string[] RequiredInventorySources);
// One catalogue entry: an exact program version, a target and the verdict with its evidence.
public sealed record CompatibilityRule(string ApplicationName, string Publisher, string Version, string TargetId,
    CompatibilityVerdict Verdict, string EvidenceReference, string Notes);
// The verdict for one installed program against the target, and the evidence it rests on.
public sealed record CompatibilityFinding(string ApplicationId, string ApplicationName, string Version,
    CompatibilityVerdict Verdict, string EvidenceReference, string Notes);
// The result of one assessment with the fingerprints of everything it was based on, so an approval can be tied to exactly this state.
public sealed record CompatibilityReport(string Id, string Node, string InventoryFingerprint, string TargetFingerprint,
    string CatalogueFingerprint, CompatibilityTarget Target, CompatibilityFinding[] Findings, string[] Blockers, string Fingerprint);

// Use case: an operator wants to know whether the applications installed on a node are known to work on a
// target system (operating system family, version, architecture). The node's inventory is compared with a
// catalogue the operator supplies, and every entry with a verdict must name its evidence. Matching is exact on
// name, publisher, version and target: a close match is not evidence, so anything not listed stays Unknown and
// becomes a blocker, as do an incomplete or truncated inventory.
//
// The report's fingerprint covers node, inventory, target, catalogue, findings and blockers, so a later approval
// can be tied to exactly this state, and any change to one of them invalidates it.
public static class CompatibilityAssessment
{
    private const int MaximumTargetIdLength = 128;
    private const int MaximumDescriptionLength = 2048;
    private const int MaximumRequiredSources = 32;
    private const int MaximumCatalogueRules = 10000;

    // Throws InvalidDataException when target, scope or evidence is missing, or two catalogue entries match the same version.
    public static CompatibilityReport Evaluate(
        string node,
        SystemInventory inventory,
        CompatibilityTarget target,
        CompatibilityRule[] catalogue)
    {
        inventory.Verify();
        RequireExplicitInputs(target, catalogue);

        List<string> blockers = InventoryBlockers(inventory, target);
        var findings = new List<CompatibilityFinding>();
        foreach (InstalledApplication application in inventory.Applications)
        {
            CompatibilityFinding finding = Assess(application, target, catalogue);
            findings.Add(finding);
            if (finding.Verdict != CompatibilityVerdict.Compatible)
            {
                blockers.Add(application.Id + ": " + finding.Verdict);
            }
        }
        string targetHash = Hash(target);
        string catalogueHash = CatalogueHash(catalogue);
        string fingerprint = Hash(new
        {
            node,
            inventory.Fingerprint,
            targetHash,
            catalogueHash,
            findings,
            blockers
        });
        return new CompatibilityReport(Guid.CreateVersion7().ToString(), node, inventory.Fingerprint, targetHash, catalogueHash,
            target, findings.ToArray(), blockers.ToArray(), fingerprint);
    }

    // Throws InvalidDataException for a target without id, description or inventory scope, or a rule without evidence.
    private static void RequireExplicitInputs(CompatibilityTarget target, CompatibilityRule[] catalogue)
    {
        bool targetValid = target.Id.Length is >= 1 and <= MaximumTargetIdLength
            && target.Description.Length is >= 1 and <= MaximumDescriptionLength
            && target.RequiredInventorySources.Length is >= 1 and <= MaximumRequiredSources;
        bool catalogueValid = catalogue.Length <= MaximumCatalogueRules && catalogue.All(rule =>
            Enum.IsDefined(rule.Verdict)
            && (rule.Verdict == CompatibilityVerdict.Unknown || !string.IsNullOrWhiteSpace(rule.EvidenceReference)));

        if (!targetValid || !catalogueValid)
        {
            throw new InvalidDataException("Explicit target, inventory scope and evidence-backed catalogue required.");
        }
    }

    // An inventory that is truncated or misses a required source cannot prove that nothing incompatible is installed.
    private static List<string> InventoryBlockers(SystemInventory inventory, CompatibilityTarget target)
    {
        var blockers = new List<string>();
        if (inventory.Truncated)
        {
            blockers.Add("Inventory was truncated.");
        }

        foreach (string required in target.RequiredInventorySources)
        {
            if (!inventory.Sources.Any(source => source.Name == required && source.Complete))
            {
                blockers.Add("Required inventory source incomplete: " + required);
            }
        }
        return blockers;
    }

    // Only a rule for the exact name, publisher, version and target counts; everything else is Unknown.
    // Throws InvalidDataException when two rules match the same application version and target.
    private static CompatibilityFinding Assess(InstalledApplication application, CompatibilityTarget target, CompatibilityRule[] catalogue)
    {
        CompatibilityRule[] matches = catalogue.Where(rule =>
            rule.ApplicationName == application.Name
            && rule.Publisher == application.Publisher
            && rule.Version == application.Version
            && rule.TargetId == target.Id).ToArray();
        if (matches.Length > 1)
        {
            throw new InvalidDataException("Ambiguous catalogue entries for exact application version and target.");
        }

        CompatibilityRule? match = matches.SingleOrDefault();
        CompatibilityVerdict verdict = match?.Verdict ?? CompatibilityVerdict.Unknown;
        return new CompatibilityFinding(application.Id, application.Name, application.Version, verdict,
            match?.EvidenceReference ?? "", match?.Notes ?? "No exact-version evidence for this target.");
    }

    // Sorted first, so the same rules in another order give the same hash.
    private static string CatalogueHash(CompatibilityRule[] catalogue)
    {
        return Hash(catalogue.OrderBy(rule => rule.ApplicationName, StringComparer.Ordinal)
            .ThenBy(rule => rule.Publisher, StringComparer.Ordinal).ThenBy(rule => rule.Version, StringComparer.Ordinal)
            .ThenBy(rule => rule.TargetId, StringComparer.Ordinal).ToArray());
    }

    public static string Hash<T>(T value)
    {
        string document = JsonSerializer.Serialize(value, SystemInventory.JsonOptions);
        return PairingProtocol.Hash("service-assessment/v1", document);
    }
}
