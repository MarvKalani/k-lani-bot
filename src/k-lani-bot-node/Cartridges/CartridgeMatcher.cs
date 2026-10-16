using System.Diagnostics;
using KLaniBot.Inventory;

namespace KLaniBot.Cartridges;

// How closely a cartridge's tested application version fits the installed one.
public enum CartridgeFit
{
    NoCartridge,
    VersionUnknown,
    OtherMajor,
    NeighbourSameMajor,
    NeighbourSameSeries,
    WithinDeclaredRange,
    Exact
}

// How the system a cartridge was tested on relates to the scanned machine.
public enum PlatformFit
{
    Unknown,
    OtherSystem,
    OtherArchitecture,
    Same
}

// What the app scan advises an administrator to do with a program, from the best cartridge it found.
public enum ScanRecommendation
{
    // A cartridge was written for exactly this build and system and has run on the real program.
    UseCartridge,

    // Same build and system, but the cartridge has only been tried on a test double or by observation.
    CheckThenUseCartridge,

    // A cartridge exists for this system, but the installed version could not be read: find it out first.
    DetermineVersionFirst,

    // A neighbouring version: run the observation entrypoint and the postconditions first and treat it as partial.
    TryNeighbourVersion,

    // A cartridge for this program exists, but for another operating system: the recipes will not carry over unchecked.
    CheckOtherPlatformCartridge,

    // Nothing usable exists: write a cartridge, with computer use as the author.
    AuthorCartridge
}

// The best cartridge for one installed program, how well its tested version and platform fit, and what the scan recommends.
public sealed record CartridgeMatchResult(
    CartridgeOffer Offer,
    CartridgeFit Fit,
    VersionDirection Direction,
    PlatformFit Platform,
    ScanRecommendation Recommendation,
    int OtherOffers);

// Use case: given an installed program, pick the cartridge that is closest to it and say what to do with it.
// "Closest" means: same system first, then the nearest version, then the more thoroughly tested cartridge,
// then the newer cartridge. The result is advice for a person; no cartridge is installed or run by it.
// How well one cartridge's tested version fits the installed version, and on which side of it the installed one lies.
public sealed record VersionFit(CartridgeFit Fit, VersionDirection Direction)
{
    public static readonly VersionFit Unknown = new(CartridgeFit.VersionUnknown, VersionDirection.Unknown);
}

// The app scan asks here which cartridge suits an installed program. The nearest tested version on the same system wins; the result says
// how near, so a neighbour is never presented as exact.
public static class CartridgeMatcher
{
    // An offer with its platform and version fit, as compared while the best one is chosen.
    private sealed record RankedOffer(CartridgeOffer Offer, PlatformFit Platform, VersionFit Fit);

    public static CartridgeMatchResult? Best(IReadOnlyList<CartridgeOffer> offers, ApplicationVersion installed, ScanMachineInfo machine)
    {
        if (offers.Count == 0)
        {
            return null;
        }
        var ranked = offers
            .Select(offer => new RankedOffer(offer, PlatformOf(offer, machine), FitOf(offer, installed)))
            .OrderByDescending(item => item.Platform)
            .ThenByDescending(item => item.Fit.Fit)
            .ThenByDescending(item => item.Offer.Validation)
            .ThenByDescending(item => ApplicationVersion.Parse(item.Offer.CartridgeVersion), VersionOrder.Instance)
            .ToArray();
        var best = ranked[0];
        var recommendation = Recommend(best.Fit.Fit, best.Platform, best.Offer.Validation);
        return new CartridgeMatchResult(best.Offer, best.Fit.Fit, best.Fit.Direction, best.Platform, recommendation, offers.Count - 1);
    }

    public static VersionFit FitOf(CartridgeOffer offer, ApplicationVersion installed)
    {
        if (!installed.IsKnown)
        {
            return VersionFit.Unknown;
        }
        if (offer.ApplicationVersion is { IsKnown: true } tested)
        {
            return FitFromComparison(installed.CompareTo(tested), exact: CartridgeFit.Exact);
        }
        return FitFromRange(offer, installed);
    }

    private static VersionFit FitFromRange(CartridgeOffer offer, ApplicationVersion installed)
    {
        var minimum = offer.MinimumVersion;
        var maximum = offer.MaximumVersionExclusive;
        bool aboveMinimum = minimum == null
            || !minimum.IsKnown
            || installed.CompareTo(minimum).Direction != VersionDirection.InstalledIsOlder;
        bool belowMaximum = maximum == null
            || !maximum.IsKnown
            || installed.CompareTo(maximum).Direction == VersionDirection.InstalledIsOlder;
        if (aboveMinimum && belowMaximum && (minimum?.IsKnown == true || maximum?.IsKnown == true))
        {
            return new VersionFit(CartridgeFit.WithinDeclaredRange, VersionDirection.Equal);
        }
        // Outside the range, the nearest bound tells how far away the installed build is.
        var bound = !aboveMinimum ? minimum : maximum;
        return bound is { IsKnown: true }
            ? FitFromComparison(installed.CompareTo(bound), exact: CartridgeFit.NeighbourSameSeries)
            : VersionFit.Unknown;
    }

    private static VersionFit FitFromComparison(VersionComparison comparison, CartridgeFit exact)
    {
        CartridgeFit fit = comparison.Relation switch
        {
            VersionRelation.Same => exact,
            VersionRelation.SameSeries => CartridgeFit.NeighbourSameSeries,
            VersionRelation.SameMajor => CartridgeFit.NeighbourSameMajor,
            VersionRelation.DifferentMajor => CartridgeFit.OtherMajor,
            VersionRelation.Unknown => CartridgeFit.VersionUnknown,
            _ => throw new UnreachableException()
        };
        return new VersionFit(fit, comparison.Direction);
    }

    private static PlatformFit PlatformOf(CartridgeOffer offer, ScanMachineInfo machine)
    {
        if (offer.Platform.Length == 0)
        {
            return PlatformFit.Unknown;
        }
        if (!string.Equals(offer.Platform, machine.OsFamily, StringComparison.OrdinalIgnoreCase))
        {
            return PlatformFit.OtherSystem;
        }
        bool anyArchitecture = offer.Architecture is "any" or "";
        return anyArchitecture
            || string.Equals(offer.Architecture, machine.Architecture, StringComparison.OrdinalIgnoreCase)
                ? PlatformFit.Same
                : PlatformFit.OtherArchitecture;
    }

    private static ScanRecommendation Recommend(CartridgeFit fit, PlatformFit platform, CartridgeValidation validation)
    {
        if (platform is PlatformFit.OtherSystem or PlatformFit.OtherArchitecture)
        {
            return ScanRecommendation.CheckOtherPlatformCartridge;
        }
        switch (fit)
        {
            case CartridgeFit.Exact:
            case CartridgeFit.WithinDeclaredRange:
                return validation == CartridgeValidation.RealAppTested
                    ? ScanRecommendation.UseCartridge
                    : ScanRecommendation.CheckThenUseCartridge;
            case CartridgeFit.NeighbourSameSeries:
            case CartridgeFit.NeighbourSameMajor:
                return ScanRecommendation.TryNeighbourVersion;
            case CartridgeFit.VersionUnknown:
                return ScanRecommendation.DetermineVersionFirst;
            case CartridgeFit.OtherMajor:
            case CartridgeFit.NoCartridge:
                return ScanRecommendation.AuthorCartridge;
            default:
                throw new UnreachableException();
        }
    }

    // Orders versions by their numbers, so offers can be ranked from the nearest tested version.
    private sealed class VersionOrder : IComparer<ApplicationVersion>
    {
        public static readonly VersionOrder Instance = new();

        public int Compare(ApplicationVersion? left, ApplicationVersion? right)
        {
            if (left == null || right == null)
            {
                return 0;
            }
            var comparison = left.CompareTo(right);
            return comparison.Direction switch
            {
                VersionDirection.InstalledIsOlder => -1,
                VersionDirection.InstalledIsNewer => 1,
                VersionDirection.Equal or VersionDirection.Unknown => 0,
                _ => throw new UnreachableException()
            };
        }
    }
}
