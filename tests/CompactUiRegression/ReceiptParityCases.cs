using System.Globalization;
using System.Text.Json.Nodes;
using KLaniBot.Common;
using KLaniBot.Transport.CompactUi;

// Cross-runtime receipt fixtures use an explicit synthetic key/proof and compare actual private store signing and validation.
internal static class ReceiptParityCases
{
    public static JsonArray Run(string file)
    {
        JsonObject corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var output = new JsonArray();
        foreach (JsonNode? node in corpus["cases"]!.AsArray())
        {
            JsonObject fixture = node!.AsObject();
            string root = Path.Combine(Path.GetTempPath(), "k-lani-receipt-parity-" + Guid.NewGuid());
            try
            {
                output.Add(new JsonObject { ["name"] = fixture["name"]!.DeepClone(), ["reply"] = RunCase(root, fixture) });
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        return output;
    }

    private static JsonObject RunCase(string root, JsonObject fixture)
    {
        string receipts = Path.Combine(root, "receipts");
        PrivateFile.CreateDirectory(receipts);
        byte[] key = Enumerable.Repeat((byte)17, CompactAcceptanceFiles.KeyBytes).ToArray();
        PrivateFile.WriteAllBytes(Path.Combine(receipts, CompactAcceptanceFiles.KeyName), key);
        JsonObject authority = fixture["authority"]!.AsObject();
        var identity = new CompactAcceptanceAuthority(authority["node"]!.GetValue<string>(), authority["principal"]!.GetValue<string>(),
            authority["connection"]!.GetValue<string>());
        CompactAcceptanceStore.TryOpen(receipts, Path.Combine(root, "exports"), identity, out var store, new Clock());
        CompactStoredFunctionCodec.TryRead(fixture["entry"], out var entry);
        CompactAcceptanceCodec.TryProof(fixture["proof"], out var proof);
        if (proof == null)
        {
            return new()
            {
                ["error"] = CompactAcceptanceNames.Error(CompactAcceptanceError.InvalidProof)
            };
        }
        if (!store.Value!.TryRecord(entry.Value!, CompatibilityParityCases.Observed(fixture["current"]!.AsObject()),
                proof, out var recorded))
        {
            return new()
            {
                ["error"] = CompactAcceptanceNames.Error(recorded.Error!.Value, recorded.Detail)
            };
        }
        return ReadRecorded(root, fixture, store.Value, entry.Value!, recorded.Value!);
    }

    private static JsonObject ReadRecorded(string root, JsonObject fixture, CompactAcceptanceStore store,
        CompactLoadedFunction entry, CompactAcceptanceReference reference)
    {
        string file = Path.Combine(store.Root, reference.Value + ".json");
        JsonObject signed = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        JsonObject original = signed.DeepClone().AsObject();
        ApplyMutation(fixture, signed);
        PrivateFile.WriteAllBytes(file, CompactRevisionFiles.Encode(signed), replace: true);
        CompactAcceptanceStore reader = store;
        if (fixture["foreign_authority"]?.GetValue<bool>() == true)
        {
            CompactAcceptanceStore.TryOpen(store.Root, Path.Combine(root, "exports"),
                store.Authority with
                {
                    Principal = "foreign"
                }, out var foreign);
            reader = foreign.Value!;
        }
        CompactObservedCompatibility observed = CompatibilityParityCases.Observed((fixture["observed"] ?? fixture["current"])!.AsObject());
        reader.TryRead(reference, entry, observed, out var accepted);

        return accepted.Value == null ? new()
        {
            ["error"] = CompactAcceptanceNames.Error(accepted.Error!.Value, accepted.Detail)
        }
            : new()
            {
                ["payload"] = original["payload"]!.DeepClone(),
                ["signature"] = original["signature"]!.DeepClone(),
                ["compatibility"] = CompactCompatibilityWire.Contract(accepted.Value.Contract)
            };
    }

    private static void ApplyMutation(JsonObject fixture, JsonObject signed)
    {
        string? mutation = fixture["mutation"]?.GetValue<string>();
        if (mutation == "signature")
        {
            signed["signature"] = new string('0', 64);
        }
        else if (mutation == "payload")
        {
            signed["payload"]!["authority"] = new string('0', 64);
        }
    }

    // A fixed UTC instant makes receipt payload and HMAC comparisons independent of runtime timing.
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.Parse("2026-10-08T00:00:00.000Z", CultureInfo.InvariantCulture);
        }
    }
}
