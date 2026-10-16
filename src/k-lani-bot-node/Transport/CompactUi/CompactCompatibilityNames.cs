using System.Diagnostics;
using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Compatibility reasons remain compact finite protocol values and identify only the failed contract layer.
public static class CompactCompatibilityNames
{
    public static string Error(CompactCompatibilityError error)
    {
        return error switch
        {
            CompactCompatibilityError.CaptureEvidenceMissing => "Compatibility capture requires actual wire, "
                + "action-schema and implementation fingerprints.",
            CompactCompatibilityError.UnsupportedContract => "Unsupported compatibility contract.",
            CompactCompatibilityError.InvalidApplication => "Compatibility requires the actual application variant/build.",
            CompactCompatibilityError.InvalidFingerprints => "Compatibility requires bounded schema, implementation "
                + "and scope fingerprints.",
            CompactCompatibilityError.InvalidAcceptance => "Invalid acceptance reference.",
            CompactCompatibilityError.MissingDescriptors => "Observed native tool descriptors are required.",
            CompactCompatibilityError.AmbiguousDescriptors => "Ambiguous or incomplete native tool descriptors.",
            _ => throw new UnreachableException()
        };
    }

    public static JsonObject Verdict(CompactCompatibilityVerdict verdict)
    {
        string status = verdict.Status switch
        {
            CompactCompatibilityStatus.Reusable => "reusable",
            CompactCompatibilityStatus.NeedsRevalidation => "needs_revalidation",
            CompactCompatibilityStatus.Incompatible => "incompatible",
            _ => throw new UnreachableException()
        };
        var result = new JsonObject { ["status"] = status, ["reason"] = Reason(verdict.Reason) };
        if (verdict.Action != null)
        {
            result["action"] = ToolNames.Of(verdict.Action.Value);
        }
        else if (verdict.Capability != null)
        {
            result["action"] = verdict.Capability;
        }

        return result;
    }

    public static string Reason(CompactCompatibilityReason reason)
    {
        return reason switch
        {
            CompactCompatibilityReason.IdentityUnknown => "identity_unknown",
            CompactCompatibilityReason.ApplicationScopeChanged => "application_scope_changed",
            CompactCompatibilityReason.FunctionScopeChanged => "function_scope_changed",
            CompactCompatibilityReason.WireVersionUnknown => "wire_version_unknown",
            CompactCompatibilityReason.WireVersionChanged => "wire_version_changed",
            CompactCompatibilityReason.ActionContractsUnknown => "action_contracts_unknown",
            CompactCompatibilityReason.RequiredActionAbsent => "required_action_absent",
            CompactCompatibilityReason.RequiredActionSchemaChanged => "required_action_schema_changed",
            CompactCompatibilityReason.ImplementationUnknown => "implementation_unknown",
            CompactCompatibilityReason.ImplementationChanged => "implementation_changed",
            CompactCompatibilityReason.VerifiedContractUnchanged => "verified_contract_unchanged",
            CompactCompatibilityReason.LegacyApplicationScopeChanged => "legacy_application_scope_changed",
            CompactCompatibilityReason.LegacyContractEvidenceMissing => "legacy_contract_evidence_missing",
            CompactCompatibilityReason.LegacyExactVersionOnly => "legacy_exact_version_only",
            CompactCompatibilityReason.TestedNativeApiRangeMissing => "tested_native_api_range_missing",
            CompactCompatibilityReason.NativeApiVersionUnknown => "native_api_version_unknown",
            CompactCompatibilityReason.NativeApiBuildOutsideTestedRange => "native_api_build_outside_tested_range",
            CompactCompatibilityReason.RequiredNativeApiProbeMissing => "required_native_api_probe_missing",
            CompactCompatibilityReason.TestedNativeApiRangeAndProbes => "tested_native_api_range_and_probes",
            CompactCompatibilityReason.InvalidRecordedContract => "invalid_recorded_contract",
            _ => throw new UnreachableException()
        };
    }
}
