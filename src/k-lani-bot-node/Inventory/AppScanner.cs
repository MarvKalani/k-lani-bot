using KLaniBot.Cartridges;

namespace KLaniBot.Inventory;

// Use case: an administrator wants to know what is installed on the machines in their care and which of
// it the product could already operate: for each program the closest cartridge, whether it is running,
// and what other ways to control it exist. Programs with no cartridge are the candidates for writing one
// with computer use; targets that are not installed are listed so a cartridge for them is not forgotten.
//
// The scan reads a list of installed programs (from this machine, from a stored inventory of another
// node, or from names a person typed) and compares it with two catalogues. It starts nothing, changes
// nothing and installs no cartridge. Its output lists installed software and is therefore stored private.
public static class AppScanner
{
    public static AppScanReport Scan(
        ScanMachineInfo machine,
        ScanMode mode,
        IReadOnlyList<ApplicationEvidence> installed,
        IReadOnlyList<ApplicationTarget> targets,
        IReadOnlyList<CartridgeOffer> cartridges,
        IReadOnlySet<string>? runningProcessNames,
        IReadOnlyList<string> limitations,
        DateTimeOffset? capturedAt = null)
    {
        var targetOfEvidence = AssignTargets(machine.OsFamily, installed, targets);
        var rows = new List<ScanRow>();
        foreach (var evidence in installed)
        {
            targetOfEvidence.TryGetValue(evidence, out ApplicationTarget? target);
            if (mode == ScanMode.TargetsOnly && target == null)
            {
                continue;
            }
            rows.Add(BuildRow(machine, evidence, target, cartridges, runningProcessNames));
        }
        var foundTargets = targetOfEvidence.Values.Select(target => target.Id).ToHashSet(StringComparer.Ordinal);
        var missing = targets
            .Where(target => !foundTargets.Contains(target.Id))
            .Select(target => DescribeMissing(target, cartridges))
            .ToArray();
        return new AppScanReport(
            capturedAt ?? DateTimeOffset.UtcNow,
            machine,
            mode,
            targets.Count,
            cartridges.Count,
            SortRows(rows),
            missing,
            limitations.ToArray());
    }

    // Each installed entry belongs to at most one target. A technical identifier beats a name, and the
    // earlier target in the catalogue wins a tie, so the result does not depend on hash ordering.
    private static Dictionary<ApplicationEvidence, ApplicationTarget> AssignTargets(
        string osFamily,
        IReadOnlyList<ApplicationEvidence> installed,
        IReadOnlyList<ApplicationTarget> targets)
    {
        var assigned = new Dictionary<ApplicationEvidence, ApplicationTarget>(ReferenceEqualityComparer.Instance);
        foreach (var evidence in installed)
        {
            ApplicationTarget? byName = null;
            foreach (var target in targets)
            {
                if (!TargetMatcher.TryMatch(target, osFamily, evidence, out MatchBasis basis))
                {
                    continue;
                }
                if (basis == MatchBasis.Identifier)
                {
                    assigned[evidence] = target;
                    byName = null;
                    break;
                }
                byName ??= target;
            }
            if (byName != null)
            {
                assigned[evidence] = byName;
            }
        }
        return assigned;
    }

    private static ScanRow BuildRow(
        ScanMachineInfo machine,
        ApplicationEvidence evidence,
        ApplicationTarget? target,
        IReadOnlyList<CartridgeOffer> cartridges,
        IReadOnlySet<string>? running)
    {
        var names = (target?.KnownNames() ?? []).Append(evidence.Name).ToArray();
        var offers = cartridges.Where(offer => names.Any(name => TargetMatcher.NameMatches(offer.Application, name))).ToArray();
        var best = CartridgeMatcher.Best(offers, ApplicationVersion.Parse(evidence.Version), machine);
        return new ScanRow(
            evidence.Name,
            evidence.Version,
            evidence.Publisher,
            evidence.Source,
            evidence.Location,
            evidence.Kind,
            running == null ? null : IsRunning(evidence, running),
            target?.Id,
            target?.Routes ?? [],
            best == null ? null : Summarize(best));
    }

    private static CartridgeMatchSummary Summarize(CartridgeMatchResult result)
    {
        return new CartridgeMatchSummary(
            Describe(result.Offer),
            result.Fit,
            result.Direction,
            result.Platform,
            result.Recommendation,
            result.OtherOffers);
    }

    private static MissingTarget DescribeMissing(ApplicationTarget target, IReadOnlyList<CartridgeOffer> cartridges)
    {
        var offers = cartridges
            .Where(offer => target.KnownNames().Any(name => TargetMatcher.NameMatches(offer.Application, name)))
            .Select(Describe)
            .ToArray();
        return new MissingTarget(target.Id, target.Name, target.Routes, offers);
    }

    public static OfferSummary Describe(CartridgeOffer offer)
    {
        return new OfferSummary(
            offer.CartridgeId,
            offer.CartridgeVersion,
            offer.Application,
            TestedVersionText(offer),
            offer.Platform,
            offer.Architecture,
            offer.Validation,
            offer.ReleaseApproved,
            offer.EntryPoints);
    }

    private static string TestedVersionText(CartridgeOffer offer)
    {
        if (offer.ApplicationVersion != null)
        {
            return offer.ApplicationVersion.Raw;
        }
        string minimum = offer.MinimumVersion?.Raw ?? "";
        string maximum = offer.MaximumVersionExclusive?.Raw ?? "";
        if (minimum == "" && maximum == "")
        {
            return "";
        }
        if (maximum == "")
        {
            return ">= " + minimum;
        }
        if (minimum == "")
        {
            return "< " + maximum;
        }
        return ">= " + minimum + ", < " + maximum;
    }

    // A process is matched by executable name only; the window title or command line of other programs is never read.
    private static bool IsRunning(ApplicationEvidence evidence, IReadOnlySet<string> processNames)
    {
        foreach (string identifier in evidence.Identifiers)
        {
            string name = Path.GetFileNameWithoutExtension(identifier).ToLowerInvariant();
            if (name.Length > 0 && processNames.Contains(name))
            {
                return true;
            }
        }
        return processNames.Contains(TargetMatcher.Normalize(evidence.Name).Replace(" ", ""));
    }

    private static ScanRow[] SortRows(List<ScanRow> rows)
    {
        return rows
            .OrderBy(row => row.Kind)
            .ThenBy(row => row.Match == null ? 1 : 0)
            .ThenBy(row => row.Match?.Recommendation ?? ScanRecommendation.AuthorCartridge)
            .ThenBy(row => row.TargetId == null ? 1 : 0)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
