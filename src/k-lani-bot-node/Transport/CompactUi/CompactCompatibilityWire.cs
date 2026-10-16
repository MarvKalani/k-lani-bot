using System.Diagnostics;
using System.Text.Json.Nodes;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Compatibility serialization keeps old contracts explicit and adds no approval flags inferred from a successful dispatch.
public static class CompactCompatibilityWire
{
    public const string Format = "k-lani-bot-compatibility/2";

    public static JsonObject Contract(CompactCompatibilityContract contract)
    {
        var result = new JsonObject
        {
            ["format"] = Format,
            ["node_version"] = contract.NodeVersion,
            ["node_wire_version"] = contract.Wire.Value,
            ["platform"] = contract.Platform,
            ["application"] = new JsonObject { ["key"] = contract.Application.Key, ["build"] = contract.Application.Build },
            ["access_route"] = Access(contract.Access),
            ["action_schemas"] = Schemas(contract.Actions),
            ["implementations"] = new JsonObject { ["native"] = contract.Native.Hex, ["gateway"] = contract.Gateway.Hex },
            ["selector_scope"] = contract.SelectorScope.Hex,
            ["function_scope"] = contract.FunctionScope.Hex
        };
        if (contract.Acceptance != null)
        {
            result["acceptance_ref"] = contract.Acceptance.Value.ToString();
        }

        return result;
    }

    public static string Access(CompactCompatibilityAccess access)
    {
        return access switch
        {
            CompactCompatibilityAccess.Accessibility => "accessibility",
            CompactCompatibilityAccess.AccessibilityWithKeyboard => "accessibility_with_keyboard",
            _ => throw new UnreachableException()
        };
    }

    public static JsonObject Schemas(CompactActionSchemas schemas)
    {
        var result = new JsonObject();
        foreach (KeyValuePair<Tool, CompactContractDigest> entry in schemas.Known)
        {
            result[ToolNames.Of(entry.Key)] = entry.Value.Hex;
        }
        foreach (KeyValuePair<string, CompactContractDigest> entry in schemas.Extensions)
        {
            result[entry.Key] = entry.Value.Hex;
        }

        return result;
    }

    public static CompactMcpWireVersion Wire(string value)
    {
        CompactWireEdition edition = value switch
        {
            "2025-11-25" => CompactWireEdition.November2025,
            "2025-06-18" => CompactWireEdition.June2025,
            "2025-03-26" => CompactWireEdition.March2025,
            "2024-11-05" => CompactWireEdition.November2024,
            _ => CompactWireEdition.Other
        };

        return new(edition, value);
    }
}
