using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Transport.CompactUi;

// Gateway digest regressions prove that rewording descriptions keeps stored compatibility while callable structure changes it.
internal static class CompactContractDigestCases
{
    private const int DigestLength = 64;
    private const string BaseTools = """
        [
          {
            "name": "ui_info",
            "description": "Begin here.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "pid": { "type": "integer", "description": "Process identifier.", "minimum": 1 },
                "root": { "type": "string" },
                "mode": { "type": "string", "enum": ["fast", "safe"], "default": "fast" },
                "filter": { "type": "object", "default": { "description": "Initial filter." } },
                "description": { "type": "string", "description": "Visible text." }
              },
              "required": ["pid"],
              "additionalProperties": false
            }
          }
        ]
        """;
    private const string BaseFunctions = """
        [
          {
            "name": "Info.compatibility",
            "description": "Explain the compatibility state.",
            "topic": "Info",
            "inputSchema": {
              "type": "object",
              "properties": {
                "function": { "type": "string", "description": "Function name." }
              },
              "required": ["function"],
              "additionalProperties": false
            }
          }
        ]
        """;
    private const string ActionName = "type_text";
    private const string BaseActionDescriptors = """
        [
          {
            "name": "type_text",
            "description": "Types text into the focused element.",
            "inputSchema": {
              "type": "object",
              "description": "Arguments of the action.",
              "properties": {
                "text": { "type": "string", "description": "Text to type." },
                "mode": { "type": "string", "enum": ["append", "replace"], "default": "append" },
                "description": { "type": "string", "description": "Visible label." }
              },
              "required": ["text"],
              "additionalProperties": false
            },
            "outputSchema": {
              "type": "object",
              "description": "Result of the action.",
              "properties": {
                "changed": { "type": "boolean", "description": "Whether the value changed." }
              },
              "required": ["changed"]
            }
          }
        ]
        """;
    private const string WordingFreeActionDescriptors = """
        [
          {
            "name": "type_text",
            "inputSchema": {
              "type": "object",
              "properties": { "text": { "type": "string" } },
              "required": ["text"]
            }
          }
        ]
        """;
    private static readonly string NativeOne = new('a', DigestLength);
    private static readonly string NativeTwo = new('b', DigestLength);

    public static void Run()
    {
        WordingKeepsDigest();
        CallableStructureChangesDigest();
        DescriptionNamedArgumentIsStructure();
        EmbeddedDescriptorsDigest();
        ActionSchemaDigestIgnoresWording();
        ActionSchemaDigestChangesWithStructure();
        ActionSchemaKeepsArgumentNamedDescription();
        WordingFreeActionDigestIsRaw();
    }

    private static void WordingKeepsDigest()
    {
        string digest = DigestOf(BaseTools, BaseFunctions).Hex;
        string toolWording = Replaced(BaseTools, "\"Begin here.\"", "\"Start with the selected window.\"");
        string argumentWording = Replaced(BaseTools, "\"Process identifier.\"", "\"Numeric process identifier.\"");
        string functionWording = Replaced(BaseFunctions, "\"Explain the compatibility state.\"", "\"Tell whether it may run.\"");
        string functionArgumentWording = Replaced(BaseFunctions, "\"Function name.\"", "\"Name of the function to check.\"");
        Require(DigestOf(toolWording, BaseFunctions).Hex == digest, "tool description wording keeps digest");
        Require(DigestOf(argumentWording, BaseFunctions).Hex == digest, "argument description wording keeps digest");
        Require(DigestOf(BaseTools, functionWording).Hex == digest, "function description wording keeps digest");
        Require(DigestOf(BaseTools, functionArgumentWording).Hex == digest, "function argument wording keeps digest");
    }

    private static void CallableStructureChangesDigest()
    {
        string digest = DigestOf(BaseTools, BaseFunctions).Hex;
        string added = Replaced(BaseTools, "\"root\": { \"type\": \"string\" },",
            "\"root\": { \"type\": \"string\" }, \"extra\": { \"type\": \"string\" },");
        string removed = Replaced(BaseTools, "\"root\": { \"type\": \"string\" },", "");
        string requiredChanged = Replaced(BaseTools, "\"required\": [\"pid\"]", "\"required\": [\"pid\", \"root\"]");
        string typeChanged = Replaced(BaseTools, "\"type\": \"integer\"", "\"type\": \"string\"");
        string enumGained = Replaced(BaseTools, "[\"fast\", \"safe\"]", "[\"fast\", \"safe\", \"slow\"]");
        string enumLost = Replaced(BaseTools, "[\"fast\", \"safe\"]", "[\"fast\"]");
        string toolRenamed = Replaced(BaseTools, "\"name\": \"ui_info\"", "\"name\": \"ui_status\"");
        string functionRenamed = Replaced(BaseFunctions, "\"name\": \"Info.compatibility\"", "\"name\": \"Info.compat\"");
        Require(DigestOf(added, BaseFunctions).Hex != digest, "added argument changes digest");
        Require(DigestOf(removed, BaseFunctions).Hex != digest, "removed argument changes digest");
        Require(DigestOf(requiredChanged, BaseFunctions).Hex != digest, "required list change changes digest");
        Require(DigestOf(typeChanged, BaseFunctions).Hex != digest, "type change changes digest");
        Require(DigestOf(enumGained, BaseFunctions).Hex != digest, "gained enum value changes digest");
        Require(DigestOf(enumLost, BaseFunctions).Hex != digest, "lost enum value changes digest");
        Require(DigestOf(toolRenamed, BaseFunctions).Hex != digest, "renamed tool changes digest");
        Require(DigestOf(BaseTools, functionRenamed).Hex != digest, "renamed function changes digest");
        Require(DigestOf("[]", BaseFunctions).Hex != digest, "removed tool changes digest");
        Require(DigestOf(BaseTools, "[]").Hex != digest, "removed function changes digest");
        Require(CompactContractStructure.Digest(NativeTwo, Parse(BaseTools), Parse(BaseFunctions)).Hex != digest,
            "changed native implementation changes digest");
    }

    private static void DescriptionNamedArgumentIsStructure()
    {
        string digest = DigestOf(BaseTools, BaseFunctions).Hex;
        string argumentWording = Replaced(BaseTools, "\"Visible text.\"", "\"Text shown in the field.\"");
        string argumentRenamed = Replaced(BaseTools, "\"description\": { \"type\": \"string\"", "\"label\": { \"type\": \"string\"");
        string dataWording = Replaced(BaseTools, "\"Initial filter.\"", "\"Empty filter.\"");
        Require(DigestOf(argumentWording, BaseFunctions).Hex == digest, "wording of an argument named description keeps digest");
        Require(DigestOf(argumentRenamed, BaseFunctions).Hex != digest, "renaming an argument named description changes digest");
        Require(DigestOf(dataWording, BaseFunctions).Hex != digest, "data under default is not projected away");
        ProjectionKeepsArgumentNamedDescription();
    }

    private static void ProjectionKeepsArgumentNamedDescription()
    {
        JsonArray original = Parse(BaseTools);
        JsonArray projected = CompactContractStructure.WithoutDescriptions(original);
        JsonObject projectedProperties = projected[0]!["inputSchema"]!["properties"]!.AsObject();
        JsonObject argumentSchema = projectedProperties["description"]!.AsObject();
        Require(!projected[0]!.AsObject().ContainsKey("description"), "tool description is projected away");
        Require(projectedProperties.ContainsKey("description") && argumentSchema["type"]?.ToString() == "string",
            "argument named description survives projection");
        Require(!argumentSchema.ContainsKey("description"), "wording inside an argument named description is projected away");
        Require(original[0]!.AsObject().ContainsKey("description"), "projection leaves its input unchanged");
    }

    private static void EmbeddedDescriptorsDigest()
    {
        CompactContractDigest digest = CompactGatewayPrograms.Implementation();
        Require(CompactContractHash.TryDigest(digest.Hex, out _), "embedded gateway descriptors produce a 64-hex digest");
        Require(CompactGatewayPrograms.Implementation().Hex == digest.Hex, "embedded gateway digest is deterministic");
        Require(HasStringDescription(CompactGatewayTools.Descriptors()), "embedded tool descriptions exist before projection");
        Require(!HasStringDescription(CompactContractStructure.WithoutDescriptions(CompactGatewayTools.Descriptors())),
            "embedded tool descriptions are projected away");
        Require(!HasStringDescription(CompactContractStructure.WithoutDescriptions(CompactGatewayFunctionTools.All)),
            "embedded function descriptions are projected away");
    }

    private static void ActionSchemaDigestIgnoresWording()
    {
        string digest = ActionDigest(BaseActionDescriptors);
        string toolWording = Replaced(BaseActionDescriptors, "\"Types text into the focused element.\"", "\"Enter text.\"");
        string propertyWording = Replaced(BaseActionDescriptors, "\"Text to type.\"", "\"Characters to enter.\"");
        string inputWording = Replaced(BaseActionDescriptors, "\"Arguments of the action.\"", "\"Arguments.\"");
        string outputWording = Replaced(BaseActionDescriptors, "\"Result of the action.\"", "\"Outcome.\"");
        string outputPropertyWording = Replaced(BaseActionDescriptors, "\"Whether the value changed.\"", "\"True when edited.\"");
        Require(ActionDigest(toolWording) == digest, "tool description wording keeps action schema digest");
        Require(ActionDigest(propertyWording) == digest, "input property wording keeps action schema digest");
        Require(ActionDigest(inputWording) == digest, "input schema wording keeps action schema digest");
        Require(ActionDigest(outputWording) == digest, "output schema wording keeps action schema digest");
        Require(ActionDigest(outputPropertyWording) == digest, "output property wording keeps action schema digest");
    }

    private static void ActionSchemaDigestChangesWithStructure()
    {
        string digest = ActionDigest(BaseActionDescriptors);
        string propertyAdded = Replaced(BaseActionDescriptors, "\"mode\": {", "\"speed\": { \"type\": \"integer\" }, \"mode\": {");
        string propertyRemoved = Replaced(BaseActionDescriptors,
            "\"mode\": { \"type\": \"string\", \"enum\": [\"append\", \"replace\"], \"default\": \"append\" },", "");
        string requiredChanged = Replaced(BaseActionDescriptors, "\"required\": [\"text\"]", "\"required\": [\"text\", \"mode\"]");
        string outputTypeChanged = Replaced(BaseActionDescriptors, "\"type\": \"boolean\"", "\"type\": \"string\"");
        string enumGained = Replaced(BaseActionDescriptors, "[\"append\", \"replace\"]", "[\"append\", \"replace\", \"insert\"]");
        string enumLost = Replaced(BaseActionDescriptors, "[\"append\", \"replace\"]", "[\"append\"]");
        string outputPropertyAdded = Replaced(BaseActionDescriptors, "\"changed\": { \"type\": \"boolean\"",
            "\"count\": { \"type\": \"integer\" }, \"changed\": { \"type\": \"boolean\"");
        Require(ActionDigest(propertyAdded) != digest, "added input property changes action schema digest");
        Require(ActionDigest(propertyRemoved) != digest, "removed input property changes action schema digest");
        Require(ActionDigest(requiredChanged) != digest, "required list change changes action schema digest");
        Require(ActionDigest(outputTypeChanged) != digest, "output type change changes action schema digest");
        Require(ActionDigest(enumGained) != digest, "gained enum value changes action schema digest");
        Require(ActionDigest(enumLost) != digest, "lost enum value changes action schema digest");
        Require(ActionDigest(outputPropertyAdded) != digest, "added output property changes action schema digest");
    }

    private static void ActionSchemaKeepsArgumentNamedDescription()
    {
        string digest = ActionDigest(BaseActionDescriptors);
        string argumentWording = Replaced(BaseActionDescriptors, "\"Visible label.\"", "\"Caption.\"");
        string argumentRenamed = Replaced(BaseActionDescriptors, "\"description\": { \"type\": \"string\"",
            "\"label\": { \"type\": \"string\"");
        string argumentTypeChanged = Replaced(BaseActionDescriptors, "\"description\": { \"type\": \"string\"",
            "\"description\": { \"type\": \"integer\"");
        Require(ActionDigest(argumentWording) == digest, "wording of an argument named description keeps action schema digest");
        Require(ActionDigest(argumentRenamed) != digest, "renaming an argument named description changes action schema digest");
        Require(ActionDigest(argumentTypeChanged) != digest, "type of an argument named description changes action schema digest");
    }

    // Without any description the projection is the identity, so the action digest equals the raw schema digest.
    private static void WordingFreeActionDigestIsRaw()
    {
        JsonObject tool = Parse(WordingFreeActionDescriptors)[0]!.AsObject();
        string raw = CompactContractHash.Of(new JsonObject
        {
            ["inputSchema"] = tool["inputSchema"]!.DeepClone(),
            ["outputSchema"] = null
        }).Hex;
        Require(ActionDigest(WordingFreeActionDescriptors) == raw, "wording-free action schema digest is the raw digest");
    }

    private static string ActionDigest(string descriptors)
    {
        bool parsed = CompactContractHash.TrySchemas(Parse(descriptors), out var attempt);
        Require(parsed && attempt.Value != null, "action descriptors produce action schemas");
        Require(ToolNames.TryParse(ActionName, out Tool action), "test action is a known tool");

        return attempt.Value!.Known[action].Hex;
    }

    private static CompactContractDigest DigestOf(string tools, string functions)
    {
        return CompactContractStructure.Digest(NativeOne, Parse(tools), Parse(functions));
    }

    private static JsonArray Parse(string text)
    {
        return JsonNode.Parse(text)!.AsArray();
    }

    private static string Replaced(string text, string oldText, string newText)
    {
        Require(text.Contains(oldText, StringComparison.Ordinal), "mutation target exists: " + oldText);

        return text.Replace(oldText, newText, StringComparison.Ordinal);
    }

    private static bool HasStringDescription(JsonNode? node)
    {
        if (node is JsonArray items)
        {
            return items.Any(HasStringDescription);
        }
        if (node is not JsonObject record)
        {
            return false;
        }

        return record.Any(entry => IsStringDescription(entry) || HasStringDescription(entry.Value));
    }

    private static bool IsStringDescription(KeyValuePair<string, JsonNode?> entry)
    {
        return entry.Key == "description" && entry.Value?.GetValueKind() == JsonValueKind.String;
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Contract digest regression failed: " + name);
        }
    }
}
