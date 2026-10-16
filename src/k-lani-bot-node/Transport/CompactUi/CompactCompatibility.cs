using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Function compatibility is assessed before input using actual observed contract fingerprints and the unchanged recorded scope.
public static class CompactCompatibility
{
    public static bool TryCapture(CompactFunctionDraft function, CompactObservedCompatibility current,
        out CompactCompatibilityAttempt<CompactCompatibilityContract> attempt)
    {
        attempt = new(null, CompactCompatibilityError.CaptureEvidenceMissing);
        IReadOnlyList<Tool> required = CompactContractHash.RequiredActions(function);
        if (string.IsNullOrEmpty(current.NodeVersion) || current.NodeVersion == "unknown" || current.Application == null
            || string.IsNullOrEmpty(current.Application.Build) || current.Application.Build == "unknown"
            || current.Wire == null || CompactCompatibilityWire.Wire(current.Wire.Value).Edition == CompactWireEdition.Other
            || !CompactContractHash.TryDigest(current.Native?.Hex, out var native)
            || !CompactContractHash.TryDigest(current.Gateway?.Hex, out var gateway)
            || current.Actions == null || required.Any(action =>
                !CompactContractHash.TryDigest(current.Actions.Known.GetValueOrDefault(action)?.Hex, out _)))
        {
            return false;
        }
        var actions = new CompactActionSchemas(required.ToDictionary(action => action, action => current.Actions.Known[action]),
            new Dictionary<string, CompactContractDigest>());
        CompactCompatibilityAccess access = function.Steps.Any(step => step.Route == CompactRecipeRoute.FocusKeyboard)
            ? CompactCompatibilityAccess.AccessibilityWithKeyboard : CompactCompatibilityAccess.Accessibility;
        attempt = new(new(current.NodeVersion, current.Wire, current.Platform ?? "unknown", current.Application, access,
            actions, native!, gateway!, CompactContractHash.Selectors(function), CompactContractHash.Function(function)));

        return true;
    }

    public static CompactCompatibilityVerdict Assess(CompactFunctionDraft function, CompactRecordedCompatibility recorded,
        CompactObservedCompatibility current)
    {
        if (string.IsNullOrEmpty(current.NodeVersion) || current.NodeVersion == "unknown" || current.Application == null
            || string.IsNullOrEmpty(current.Application.Build) || current.Application.Build == "unknown"
            || string.IsNullOrEmpty(current.Platform) || current.Platform == "unknown")
        {
            return new(CompactCompatibilityStatus.NeedsRevalidation, CompactCompatibilityReason.IdentityUnknown);
        }

        return recorded.Format switch
        {
            CompactCompatibilityFormat.Legacy => recorded.Legacy == null ? Invalid() : Legacy(recorded.Legacy, current),
            CompactCompatibilityFormat.Contracts2 => recorded.Contracts == null ? Invalid() : Modern(function, recorded.Contracts, current),
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private static CompactCompatibilityVerdict Legacy(CompactScanCompatibility recorded, CompactObservedCompatibility current)
    {
        if (recorded.ApplicationVersion != current.Application!.Build || recorded.Platform != current.Platform)
        {
            return new(CompactCompatibilityStatus.Incompatible, CompactCompatibilityReason.LegacyApplicationScopeChanged);
        }

        return recorded.NodeVersion == current.NodeVersion
            ? new(CompactCompatibilityStatus.Reusable, CompactCompatibilityReason.LegacyExactVersionOnly)
            : new(CompactCompatibilityStatus.NeedsRevalidation, CompactCompatibilityReason.LegacyContractEvidenceMissing);
    }

    private static CompactCompatibilityVerdict Modern(CompactFunctionDraft function, CompactCompatibilityContract recorded,
        CompactObservedCompatibility current)
    {
        if (recorded.Application != current.Application || recorded.Platform != current.Platform)
        {
            return new(CompactCompatibilityStatus.Incompatible, CompactCompatibilityReason.ApplicationScopeChanged);
        }
        if (recorded.FunctionScope != CompactContractHash.Function(function)
            || recorded.SelectorScope != CompactContractHash.Selectors(function))
        {
            return new(CompactCompatibilityStatus.Incompatible, CompactCompatibilityReason.FunctionScopeChanged);
        }
        if (current.Wire == null)
        {
            return new(CompactCompatibilityStatus.NeedsRevalidation, CompactCompatibilityReason.WireVersionUnknown);
        }
        if (recorded.Wire.Value != current.Wire.Value)
        {
            return new(CompactCompatibilityStatus.Incompatible, CompactCompatibilityReason.WireVersionChanged);
        }

        return Actions(function, recorded, current) ?? Implementations(recorded, current);
    }

    private static CompactCompatibilityVerdict? Actions(CompactFunctionDraft function, CompactCompatibilityContract recorded,
        CompactObservedCompatibility current)
    {
        foreach (Tool action in CompactContractHash.RequiredActions(function))
        {
            if (current.Actions == null)
            {
                return new(CompactCompatibilityStatus.NeedsRevalidation, CompactCompatibilityReason.ActionContractsUnknown);
            }
            if (!current.Actions.Known.TryGetValue(action, out var observed))
            {
                return new(CompactCompatibilityStatus.Incompatible, CompactCompatibilityReason.RequiredActionAbsent, action);
            }
            if (recorded.Actions.Known.GetValueOrDefault(action) != observed)
            {
                return new(CompactCompatibilityStatus.NeedsRevalidation, CompactCompatibilityReason.RequiredActionSchemaChanged, action);
            }
        }

        return null;
    }

    private static CompactCompatibilityVerdict Implementations(CompactCompatibilityContract recorded, CompactObservedCompatibility current)
    {
        if (!CompactContractHash.TryDigest(current.Native?.Hex, out _) || !CompactContractHash.TryDigest(current.Gateway?.Hex, out _))
        {
            return new(CompactCompatibilityStatus.NeedsRevalidation, CompactCompatibilityReason.ImplementationUnknown);
        }
        if (recorded.Native != current.Native || recorded.Gateway != current.Gateway)
        {
            return new(CompactCompatibilityStatus.NeedsRevalidation, CompactCompatibilityReason.ImplementationChanged);
        }

        return new(CompactCompatibilityStatus.Reusable, CompactCompatibilityReason.VerifiedContractUnchanged);
    }

    private static CompactCompatibilityVerdict Invalid()
    {
        return new(CompactCompatibilityStatus.NeedsRevalidation, CompactCompatibilityReason.InvalidRecordedContract);
    }
}
