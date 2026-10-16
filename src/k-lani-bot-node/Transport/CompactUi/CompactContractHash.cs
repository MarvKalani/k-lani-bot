using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using KLaniBot.Actions;

namespace KLaniBot.Transport.CompactUi;

// Canonical contract hashes match the JS reference, including object key ordering, ordered arrays and exact Unicode values.
public static class CompactContractHash
{
    private static readonly Regex DigestPattern = new(@"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant);

    public static bool TryDigest(string? value, out CompactContractDigest? digest)
    {
        digest = value != null && DigestPattern.IsMatch(value) ? new(value) : null;

        return digest != null;
    }

    public static CompactContractDigest Of(JsonNode? value)
    {
        byte[] content = Encoding.UTF8.GetBytes(CompactJsonText.Serialize(Canonical(value)));

        return new(Convert.ToHexStringLower(SHA256.HashData(content)));
    }

    private static JsonNode? Canonical(JsonNode? value)
    {
        if (value is JsonObject record)
        {
            IEnumerable<string> keys = record.Select(entry => entry.Key).OrderBy(key => Index(key) == null ? 1 : 0)
                .ThenBy(key => Index(key) ?? 0).ThenBy(key => key, StringComparer.Ordinal);

            return new JsonObject(keys.Select(key => new KeyValuePair<string, JsonNode?>(key, Canonical(record[key]))));
        }
        if (value is JsonArray array)
        {
            return new JsonArray(array.Select(Canonical).ToArray());
        }

        return value?.DeepClone();
    }

    private static uint? Index(string key)
    {
        return uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out uint index) && index != uint.MaxValue
            && index.ToString(CultureInfo.InvariantCulture) == key ? index : null;
    }

    public static CompactContractDigest Function(CompactFunctionDraft function)
    {
        JsonObject entry = CompactCaptureWire.Draft(function);

        return Of(new JsonObject
        {
            ["name"] = entry["name"]!.DeepClone(),
            ["steps"] = entry["steps"]!.DeepClone(),
            ["outputs"] = entry["outputs"]!.DeepClone(),
            ["inputSchema"] = entry["inputSchema"]!.DeepClone()
        });
    }

    public static CompactContractDigest Selectors(CompactFunctionDraft function)
    {
        return Of(new JsonArray(function.Steps.Select(step => (JsonNode?)CompactDiscoveryWire.Selector(step.Selector)).ToArray()));
    }

    public static IReadOnlyList<Tool> RequiredActions(CompactFunctionDraft function)
    {
        var actions = new HashSet<Tool> { Tool.GetMachineCapabilities, Tool.ListWindows, Tool.GetUiTree };
        foreach (CompactRecipeStep step in function.Steps)
        {
            if (step.Action is CompactRecipeAction.Click or CompactRecipeAction.Select)
            {
                actions.Add(Tool.ClickElement);
            }
            if (step.Action == CompactRecipeAction.Write)
            {
                actions.Add(Tool.TypeText);
                if (step.Route == CompactRecipeRoute.FocusKeyboard)
                {
                    actions.UnionWith([Tool.ClickElement, Tool.SetClipboard, Tool.Hotkey]);
                }
            }
        }

        return actions.OrderBy(ToolNames.Of, StringComparer.Ordinal).ToArray();
    }

    public static bool TrySchemas(JsonNode? descriptors, out CompactCompatibilityAttempt<CompactActionSchemas> attempt)
    {
        attempt = new(null, CompactCompatibilityError.MissingDescriptors);
        if (descriptors is not JsonArray tools)
        {
            return false;
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        var known = new Dictionary<Tool, CompactContractDigest>();
        var extensions = new Dictionary<string, CompactContractDigest>(StringComparer.Ordinal);
        foreach (JsonNode? node in tools)
        {
            if (node is not JsonObject tool || !CompactDiscoveryDocumentCodec.Text(tool, "name", out string name)
                || tool["inputSchema"] == null || !names.Add(name))
            {
                attempt = new(null, CompactCompatibilityError.AmbiguousDescriptors);
                return false;
            }
            CompactContractDigest digest = Of(new JsonObject
            {
                ["inputSchema"] = CompactContractStructure.SchemaWithoutDescriptions(tool["inputSchema"]),
                ["outputSchema"] = CompactContractStructure.SchemaWithoutDescriptions(tool["outputSchema"])
            });
            if (ToolNames.TryParse(name, out Tool action))
            {
                known.Add(action, digest);
            }
            else
            {
                extensions.Add(name, digest);
            }
        }
        attempt = new(new(known, extensions));

        return true;
    }
}
