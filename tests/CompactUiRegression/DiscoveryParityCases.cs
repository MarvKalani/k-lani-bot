using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Shared synthetic tree inputs compare scanner content, structural hashes, history, restarts and precise semantic failures.
internal static class DiscoveryParityCases
{
    private static readonly CompactProgramIdentity Application = new("form-fixture", "1.0");
    private static readonly CompactScanCompatibility Compatibility = new("fixture-node", "fixture-platform", "1.0");
    private static readonly CompactNativeWindow Observed = new(10, "fixture", "1.0", "Form", 100, "start", CompactWindowKind.Main);

    public static JsonArray Run(string file)
    {
        JsonObject corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var output = new JsonArray();
        foreach (JsonNode? fixture in corpus["cases"]!.AsArray())
        {
            string directory = Path.Combine(Path.GetTempPath(), "k-lani-discovery-parity-" + Guid.NewGuid());
            try
            {
                output.Add(RunFixture(fixture!.AsObject(), directory));
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        return output;
    }

    private static JsonObject RunFixture(JsonObject fixture, string directory)
    {
        CompactFormScanner scanner = Open(directory);
        var replies = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        var steps = new JsonArray();
        foreach (JsonNode? node in fixture["steps"]!.AsArray())
        {
            JsonObject step = node!.AsObject();
            if (step["restart"]?.GetValue<bool>() == true)
            {
                scanner = Open(directory);
            }
            JsonObject arguments = step["arguments"]!.DeepClone().AsObject();
            if (step["state_from"] is JsonValue alias)
            {
                arguments["state"] = replies[alias.GetValue<string>()]["state"]!.DeepClone();
            }
            JsonObject reply = Call(scanner, step, arguments);
            replies[step["name"]!.GetValue<string>()] = reply;
            steps.Add(new JsonObject { ["name"] = step["name"]!.DeepClone(), ["reply"] = Normalize(reply) });
        }

        string file = Path.Combine(directory, CompactObservedFormStore.FileName);

        return new()
        {
            ["name"] = fixture["name"]!.DeepClone(),
            ["steps"] = steps,
            ["document"] = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) : null
        };
    }

    private static JsonObject Call(CompactFormScanner scanner, JsonObject step, JsonObject arguments)
    {
        if (step["action"]!.GetValue<string>() == "read")
        {
            CompactDiscoveryCodec.TryLookup(arguments, out var lookup);
            if (lookup.Error != null)
            {
                return Error(lookup.Error.Value, lookup.Detail);
            }
            scanner.TryRead(lookup.Value!, out var read);
            return read.Value ?? Error(read.Error!.Value, read.Detail);
        }
        CompactDiscoveryCodec.TryScan(arguments, out var options);
        if (options.Error != null)
        {
            return Error(options.Error.Value, options.Detail);
        }
        CompactTreeNode[] nodes = Nodes(step);
        if (!scanner.TryScan(nodes, options.Value!, Observed, Compatibility, out var scan))
        {
            return Error(scan.Error!.Value, scan.Detail);
        }
        JsonObject reply = CompactDiscoveryWire.Scan(scan.Value!);
        if (step["format"]?.GetValue<bool>() == true)
        {
            return Format(reply, scan.Value!, options.Value!);
        }

        return reply;
    }

    private static JsonObject Format(JsonObject reply, CompactScanResult result, CompactScanOptions options)
    {
        reply["success"] = true;
        if (options.Result.Mode == CompactResultMode.Values)
        {
            var outputs = new JsonObject();
            foreach (CompactScanContent field in result.Observation.Contents!.Fields)
            {
                outputs[field.Key] = field.Value ?? field.Name;
            }
            reply["outputs"] = outputs;
        }
        var store = new CompactResultStore();
        var formatter = new CompactResultFormatter(store);

        return formatter.Format(new("node", Guid.NewGuid()), reply, options.Result, scan: new(
            result.Observation.Fields.Count, result.Observation.Contents?.Fields.Count ?? 0));
    }

    private static CompactTreeNode[] Nodes(JsonObject step)
    {
        return step["nodes"]!.AsArray().Select(node => new CompactTreeNode(
            node!["depth"]?.GetValue<int>() ?? 0, node["id"]!.GetValue<int>(), CompactRole.Parse(node["role"]!.GetValue<string>()),
            node["name"]!.GetValue<string>(), node["autoId"]?.GetValue<string>() ?? "",
            Repeat(node["value"]?.GetValue<string>(), node["repeat_value"]?.GetValue<int>() ?? 1),
            node["enabled"]?.GetValue<bool>() ?? true, node["offscreen"]?.GetValue<bool>() ?? false)).ToArray();
    }

    private static string? Repeat(string? value, int count)
    {
        return value == null ? null : string.Concat(Enumerable.Repeat(value, count));
    }

    private static CompactFormScanner Open(string directory)
    {
        if (!CompactObservedFormStore.TryOpen(directory, Application, out var loaded))
        {
            throw new InvalidOperationException("Cannot open synthetic scanner store: " + loaded.Detail);
        }

        return new(loaded.Value!);
    }

    private static JsonNode? Normalize(JsonNode? node)
    {
        if (node is JsonObject document)
        {
            return new JsonObject(document.Select(entry => new KeyValuePair<string, JsonNode?>(entry.Key,
                entry.Key == "evidence_ref" ? JsonValue.Create("$evidence") : Normalize(entry.Value))));
        }
        if (node is JsonArray array)
        {
            return new JsonArray(array.Select(Normalize).ToArray());
        }

        return node?.DeepClone();
    }

    private static JsonObject Error(CompactDiscoveryError error, string? detail)
    {
        return new()
        {
            ["error"] = CompactDiscoveryNames.Error(error, detail)
        };
    }
}
