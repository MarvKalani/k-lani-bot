using System.Globalization;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// Native API reuse records an explicitly tested application range; availability of COM or VBA alone is not compatibility evidence.
public sealed record CompactTestedApiRange(string? Minimum, string? Maximum, string? Evidence);

// One application API contract names only the capabilities required by the selected function.
public sealed record CompactNativeApiContract(string ApplicationKey, string Platform, string Function,
    CompactTestedApiRange? TestedRange, IReadOnlyList<string>? RequiredCapabilities);

// A fresh probe's origin is typed before an application API may use it as evidence.
public enum CompactApiProbeSource
{
    NativeApi,
    Other
}

// Non-mutating probes bind their actual result and evidence to one function; unknown values never imply availability.
public sealed record CompactNativeApiProbe(CompactApiProbeSource Source, string? Function, bool? Mutating,
    bool? Available, string? Evidence);

// Native range assessment uses the current application version and freshly obtained function-level probes.
public sealed record CompactNativeApiObservation(CompactProgramIdentity? Application, string? Platform,
    IReadOnlyDictionary<string, CompactNativeApiProbe>? Probes);

// Native API scope assessment requires tested numeric ranges and current non-mutating probes before reporting reusable.
public static class CompactNativeApiScope
{
    private static readonly Regex VersionPattern = new(@"\A[0-9]{1,9}(\.[0-9]{1,9}){0,3}\z", RegexOptions.CultureInvariant);
    private const int VersionParts = 4;
    private const int MaximumCapabilities = 16;

    public static CompactCompatibilityVerdict Assess(CompactNativeApiContract contract, CompactNativeApiObservation current)
    {
        if (current.Application == null || string.IsNullOrEmpty(current.Application.Build) || current.Application.Build == "unknown")
        {
            return new(CompactCompatibilityStatus.NeedsRevalidation, CompactCompatibilityReason.IdentityUnknown);
        }
        if (contract.ApplicationKey != current.Application.Key || contract.Platform != current.Platform)
        {
            return new(CompactCompatibilityStatus.Incompatible, CompactCompatibilityReason.ApplicationScopeChanged);
        }
        if (contract.TestedRange == null || string.IsNullOrEmpty(contract.TestedRange.Evidence)
            || contract.RequiredCapabilities == null || contract.RequiredCapabilities.Count is < 1 or > MaximumCapabilities)
        {
            return new(CompactCompatibilityStatus.NeedsRevalidation, CompactCompatibilityReason.TestedNativeApiRangeMissing);
        }
        if (!TryVersion(contract.TestedRange.Minimum, out var minimum) || !TryVersion(contract.TestedRange.Maximum, out var maximum)
            || !TryVersion(current.Application.Build, out var actual) || Compare(minimum!, maximum!) > 0)
        {
            return new(CompactCompatibilityStatus.NeedsRevalidation, CompactCompatibilityReason.NativeApiVersionUnknown);
        }
        if (Compare(actual!, minimum!) < 0 || Compare(actual!, maximum!) > 0)
        {
            return new(CompactCompatibilityStatus.Incompatible, CompactCompatibilityReason.NativeApiBuildOutsideTestedRange);
        }

        return Probes(contract, current);
    }

    private static CompactCompatibilityVerdict Probes(CompactNativeApiContract contract, CompactNativeApiObservation current)
    {
        foreach (string capability in contract.RequiredCapabilities!)
        {
            CompactNativeApiProbe? probe = current.Probes?.GetValueOrDefault(capability);
            if (probe == null || probe.Source != CompactApiProbeSource.NativeApi || probe.Function != contract.Function
                || probe.Mutating != false || probe.Available != true || string.IsNullOrEmpty(probe.Evidence))
            {
                return new(CompactCompatibilityStatus.NeedsRevalidation, CompactCompatibilityReason.RequiredNativeApiProbeMissing,
                    Capability: capability);
            }
        }

        return new(CompactCompatibilityStatus.Reusable, CompactCompatibilityReason.TestedNativeApiRangeAndProbes);
    }

    private static bool TryVersion(string? version, out IReadOnlyList<int>? parts)
    {
        parts = null;
        if (version == null || !VersionPattern.IsMatch(version))
        {
            return false;
        }
        string[] raw = version.Split('.');
        var numbers = new int[VersionParts];
        for (int index = 0; index < raw.Length; index++)
        {
            numbers[index] = int.Parse(raw[index], CultureInfo.InvariantCulture);
        }
        parts = numbers;

        return true;
    }

    private static int Compare(IReadOnlyList<int> first, IReadOnlyList<int> second)
    {
        for (int index = 0; index < VersionParts; index++)
        {
            if (first[index] != second[index])
            {
                return first[index] < second[index] ? -1 : 1;
            }
        }

        return 0;
    }
}
