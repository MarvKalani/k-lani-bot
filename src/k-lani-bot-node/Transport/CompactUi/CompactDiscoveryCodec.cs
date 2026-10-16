using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// Discovery arguments are bounded before native observation, and result projection is validated by the existing compact contract.
public static class CompactDiscoveryCodec
{
    public const int MaximumContentCharacters = 16000;
    public const int DefaultContentCharacters = 2000;
    private static readonly Regex FormName = new(@"\A[a-z][a-z0-9._-]{0,79}\z", RegexOptions.CultureInvariant);
    private static readonly string[] ScanFields = ["pid", "form", "window", "root", "contents", "max_chars", "result_mode"];

    public static bool ValidName(string? name)
    {
        return name != null && FormName.IsMatch(name);
    }

    public static bool TryScan(JsonNode? arguments, out CompactDiscoveryAttempt<CompactScanOptions> attempt)
    {
        if (arguments is not JsonObject document)
        {
            attempt = new(null, CompactDiscoveryError.InvalidArguments);
            return false;
        }
        if (!CompactResultOptionsCodec.TryParse(document, null, out var result, out string? resultError))
        {
            attempt = new(null, CompactDiscoveryError.InvalidArguments, resultError);
            return false;
        }
        if (document.Any(entry => !ScanFields.Contains(entry.Key, StringComparer.Ordinal))
            || !TryInteger(document["pid"], int.MaxValue, out int processId)
            || !TryText(document, "form", out string? name) || !ValidName(name)
            || !TryWindowHandle(document, out long? window)
            || !TryText(document, "root", out string? root) || !TryContents(document, out bool contents)
            || !TryCharacters(document, out int characters))
        {
            attempt = new(null, CompactDiscoveryError.InvalidArguments);
            return false;
        }
        attempt = new(new(processId, name!, window, root, contents, characters, result!));

        return true;
    }

    // An absent window follows the remembered or only window. A window is the node's native handle, never a caption, so a text value
    // or an explicit null is refused.
    private static bool TryWindowHandle(JsonObject document, out long? window)
    {
        window = null;
        if (!document.ContainsKey("window"))
        {
            return true;
        }
        if (document["window"] is not JsonValue value || !value.TryGetValue<long>(out long handle) || handle == 0)
        {
            return false;
        }
        window = handle;

        return true;
    }

    public static bool TryLookup(JsonNode? arguments, out CompactDiscoveryAttempt<CompactFormLookup> attempt)
    {
        if (arguments is not JsonObject document || document.Any(entry => entry.Key is not "form" and not "state")
            || !TryText(document, "form", out string? name) || !ValidName(name)
            || !TryText(document, "state", out string? state))
        {
            attempt = new(null, CompactDiscoveryError.InvalidLookup);
            return false;
        }
        attempt = new(new(name!, state));

        return true;
    }

    private static bool TryContents(JsonObject document, out bool contents)
    {
        contents = false;

        return !document.ContainsKey("contents") || document["contents"] is JsonValue value && value.TryGetValue(out contents);
    }

    private static bool TryCharacters(JsonObject document, out int characters)
    {
        characters = DefaultContentCharacters;

        return !document.ContainsKey("max_chars") || TryInteger(document["max_chars"], MaximumContentCharacters, out characters);
    }

    private static bool TryInteger(JsonNode? node, int maximum, out int number)
    {
        number = 0;
        if (node is not JsonValue value || value.GetValueKind() != JsonValueKind.Number
            || !double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || !double.IsFinite(parsed) || Math.Truncate(parsed) != parsed || parsed < 1 || parsed > maximum)
        {
            return false;
        }
        number = (int)parsed;

        return true;
    }

    private static bool TryText(JsonObject document, string name, out string? text)
    {
        text = null;

        return !document.ContainsKey(name) || document[name] is JsonValue value && value.TryGetValue(out text) && text != null;
    }
}

// Stored form lookup selects one named form and optionally a previously observed structural state.
public sealed record CompactFormLookup(string Form, string? State);
