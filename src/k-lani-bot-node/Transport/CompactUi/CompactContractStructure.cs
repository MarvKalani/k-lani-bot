using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Gateway digests bind callable structure, so rewording a description does not force stored functions to revalidate.
public static class CompactContractStructure
{
    private const string DescriptionKeyword = "description";
    private static readonly string[] SchemaMapKeywords = ["properties", "$defs", "definitions"];
    private static readonly string[] SchemaListKeywords = ["anyOf", "oneOf", "allOf"];

    public static CompactContractDigest Digest(string? native, JsonArray tools, JsonArray functions)
    {
        return CompactContractHash.Of(new JsonObject
        {
            ["implementation"] = native,
            ["tools"] = WithoutDescriptions(tools),
            ["functions"] = WithoutDescriptions(functions)
        });
    }

    // The walk copies first, because the embedded descriptors are shared and still served to clients with their text.
    public static JsonArray WithoutDescriptions(JsonArray descriptors)
    {
        JsonArray copy = descriptors.DeepClone().AsArray();

        foreach (JsonNode? descriptor in copy)
        {
            if (descriptor is JsonObject descriptorObject)
            {
                RemoveStringDescription(descriptorObject);
                ProjectSchema(descriptorObject["inputSchema"]);
                ProjectSchema(descriptorObject["outputSchema"]);
            }
        }

        return copy;
    }

    // An action schema is digested with the same projection as the descriptors, so its wording never changes the digest.
    public static JsonNode? SchemaWithoutDescriptions(JsonNode? schema)
    {
        JsonNode? copy = schema?.DeepClone();

        ProjectSchema(copy);

        return copy;
    }

    // Only a string in a schema position is wording; an argument named description is a map key and stays.
    private static void RemoveStringDescription(JsonObject schemaOrDescriptor)
    {
        if (schemaOrDescriptor[DescriptionKeyword]?.GetValueKind() == JsonValueKind.String)
        {
            schemaOrDescriptor.Remove(DescriptionKeyword);
        }
    }

    // Enumerations, constants, defaults and examples are data and are never descended into.
    private static void ProjectSchema(JsonNode? schema)
    {
        if (schema is not JsonObject schemaObject)
        {
            return;
        }

        RemoveStringDescription(schemaObject);
        foreach (string keyword in SchemaMapKeywords)
        {
            ProjectSchemaMap(schemaObject[keyword]);
        }
        foreach (string keyword in SchemaListKeywords)
        {
            ProjectSchemaList(schemaObject[keyword]);
        }
        ProjectItemsSchema(schemaObject["items"]);
        ProjectSchema(schemaObject["additionalProperties"]);
    }

    private static void ProjectSchemaMap(JsonNode? schemasByName)
    {
        if (schemasByName is not JsonObject schemaMap)
        {
            return;
        }

        foreach (KeyValuePair<string, JsonNode?> entry in schemaMap)
        {
            ProjectSchema(entry.Value);
        }
    }

    private static void ProjectSchemaList(JsonNode? schemas)
    {
        if (schemas is not JsonArray schemaList)
        {
            return;
        }

        foreach (JsonNode? schema in schemaList)
        {
            ProjectSchema(schema);
        }
    }

    // Items is either one schema or a positional list of schemas.
    private static void ProjectItemsSchema(JsonNode? items)
    {
        if (items is JsonArray positionalSchemas)
        {
            ProjectSchemaList(positionalSchemas);

            return;
        }

        ProjectSchema(items);
    }
}
