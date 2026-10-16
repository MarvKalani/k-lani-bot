using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// One explicit generated document loader accepts v1/v2 and preserves unknown future or malformed documents without rewriting them.
public static class CompactFunctionDocumentCodec
{
    public const string Format = "k-lani-bot-generated-ui-functions";
    public const long MaximumRevision = 9007199254740991;

    public static bool TryRead(JsonNode? node, CompactProgramIdentity application,
        out CompactFunctionAttempt<CompactFunctionDocument> attempt)
    {
        attempt = new(null, CompactFunctionError.InvalidDocument);
        if (node is not JsonObject document || !CompactDiscoveryDocumentCodec.Only(document, "format", "version", "application",
                "functions", "candidates", "revision") || !Text(document, "format", out string format) || format != Format
            || !TryInteger(document["version"], out long version) || version is not 1 and not 2
            || document["application"] is not JsonObject identity || !Text(identity, "key", out string key) || key != application.Key
            || !Text(identity, "version", out string build) || build != application.Build
            || document["functions"] is not JsonArray functions)
        {
            return false;
        }
        long revision = 1;
        if (document.ContainsKey("revision") && !TryInteger(document["revision"], out revision))
        {
            attempt = new(null, CompactFunctionError.InvalidRevision);
            return false;
        }
        if (!TryFunctions(functions, out var parsed))
        {
            attempt = new(null, parsed.Error, parsed.Detail);
            return false;
        }
        if (!TryCandidates(document, out var candidates))
        {
            attempt = new(null, candidates.Error, candidates.Detail);
            return false;
        }
        attempt = new(new(application, (int)version, revision, parsed.Value!, candidates.Value!, document.DeepClone().AsObject()));

        return true;
    }

    private static bool TryFunctions(JsonArray array, out CompactFunctionAttempt<IReadOnlyList<CompactLoadedFunction>> attempt)
    {
        var functions = new List<CompactLoadedFunction>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? node in array)
        {
            if (!CompactStoredFunctionCodec.TryRead(node, out var function))
            {
                attempt = new(null, function.Error, function.Detail);
                return false;
            }
            if (!names.Add(function.Value!.Draft.Name))
            {
                attempt = new(null, CompactFunctionError.DuplicateFunction);
                return false;
            }
            functions.Add(function.Value);
        }
        attempt = new(functions);

        return true;
    }

    private static bool TryCandidates(JsonObject document, out CompactFunctionAttempt<IReadOnlyList<CompactCaptureCandidate>> attempt)
    {
        var candidates = new List<CompactCaptureCandidate>();
        if (!document.ContainsKey("candidates"))
        {
            attempt = new(candidates);
            return true;
        }
        if (document["candidates"] is not JsonArray array)
        {
            attempt = new(null, CompactFunctionError.InvalidCandidate);
            return false;
        }
        foreach (JsonNode? node in array)
        {
            if (!TryCandidate(node, out var candidate))
            {
                attempt = new(null, candidate.Error, candidate.Detail);
                return false;
            }
            candidates.Add(candidate.Value!);
        }
        attempt = new(candidates);

        return true;
    }

    public static bool TryCandidate(JsonNode? node, out CompactFunctionAttempt<CompactCaptureCandidate> attempt)
    {
        attempt = new(null, CompactFunctionError.InvalidCandidate);
        if (node is not JsonObject document
            || !CompactDiscoveryDocumentCodec.Only(document, "name", "topic", "action", "selector", "reason")
            || !Text(document, "name", out string name) || !CompactDiscoveryCodec.ValidName(name)
            || !Text(document, "topic", out string topic) || !CompactCaptureCodec.TryTopic(topic, out var category)
            || !Text(document, "action", out string action) || !CompactStoredFunctionCodec.TryAction(action, out var kind)
            || kind is not CompactRecipeAction.Click and not CompactRecipeAction.Write)
        {
            return false;
        }
        if (!CompactStoredSelectorCodec.TryRead(document["selector"], false, out var selector))
        {
            attempt = new(null, selector.Error);
            return false;
        }
        attempt = new(new(name, category!, kind, selector.Value!, Text(document, "reason", out string reason) ? reason : ""));

        return true;
    }

    public static bool TryInteger(JsonNode? node, out long number)
    {
        number = 0;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number
            || !double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || !double.IsFinite(parsed) || Math.Truncate(parsed) != parsed || parsed < 1 || parsed > MaximumRevision)
        {
            return false;
        }
        number = (long)parsed;

        return true;
    }

    private static bool Text(JsonObject document, string field, out string text)
    {
        return CompactDiscoveryDocumentCodec.Text(document, field, out text);
    }
}
