using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Native range cases distinguish fresh function-specific probes from generic COM/VBA availability without contacting an application.
internal static class NativeApiParityCases
{
    public static JsonArray Run(string file)
    {
        JsonObject corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var output = new JsonArray();
        foreach (JsonNode? node in corpus["cases"]!.AsArray())
        {
            JsonObject fixture = node!.AsObject();
            var verdict = CompactNativeApiScope.Assess(Contract(fixture["contract"]!.AsObject()), Current(fixture["current"]!.AsObject()));
            output.Add(new JsonObject { ["name"] = fixture["name"]!.DeepClone(), ["reply"] = CompactCompatibilityNames.Verdict(verdict) });
        }

        return output;
    }

    private static CompactNativeApiContract Contract(JsonObject document)
    {
        CompactTestedApiRange? range = document["tested_range"] is JsonObject record
            ? new(record["minimum"]?.GetValue<string>(), record["maximum"]?.GetValue<string>(),
                record["evidence_ref"]?.GetValue<string>()) : null;
        string[]? capabilities = document["required_capabilities"]?.AsArray().Select(value => value!.GetValue<string>()).ToArray();

        return new(document["application_key"]!.GetValue<string>(), document["platform"]!.GetValue<string>(),
            document["function"]!.GetValue<string>(), range, capabilities);
    }

    private static CompactNativeApiObservation Current(JsonObject document)
    {
        var probes = new Dictionary<string, CompactNativeApiProbe>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, JsonNode?> entry in document["probes"]?.AsObject() ?? new JsonObject())
        {
            if (entry.Value is JsonObject probe)
            {
                probes.Add(entry.Key, new(probe["source"]?.GetValue<string>() == "native_api_probe" ? CompactApiProbeSource.NativeApi
                    : CompactApiProbeSource.Other, probe["function"]?.GetValue<string>(), probe["mutating"]?.GetValue<bool>(),
                    probe["available"]?.GetValue<bool>(), probe["evidence_ref"]?.GetValue<string>()));
            }
        }
        CompactProgramIdentity? application = document["application"] is JsonObject app
            ? new(app["key"]?.GetValue<string>() ?? "", app["version"]?.GetValue<string>() ?? "") : null;

        return new(application, document["platform"]?.GetValue<string>(), probes);
    }
}
