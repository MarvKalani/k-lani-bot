using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace KLaniBot.Common;

// Use case: the node has to recognise the same content again, regardless of the order in which an agent
// or a file lists the properties: a macro version, a retried request, an evaluation case, a rubric.
// Object properties are sorted by ordinal name before the content is hashed, so equal content always
// gives the same SHA-256.
//
// The output is part of stored data (macro hashes in the macro store, calibration reports bound to a
// rubric), so the algorithm must not change; the tests pin a reference value.
public static class CanonicalJson
{
    public static string Hash(JsonNode node)
    {
        string canonical = Sort(node)!.ToJsonString();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static JsonNode? Sort(JsonNode? value)
    {
        return value switch
        {
            JsonObject obj => new JsonObject(obj
                .OrderBy(property => property.Key, StringComparer.Ordinal)
                .Select(property => KeyValuePair.Create(property.Key, Sort(property.Value)))),
            JsonArray array => new JsonArray(array.Select(Sort).ToArray()),
            _ => value?.DeepClone()
        };
    }
}
