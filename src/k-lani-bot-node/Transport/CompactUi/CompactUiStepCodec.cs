using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Compact UI parsing validates action-specific fields before any native input, including absent versus deliberately empty text.
public static class CompactUiStepCodec
{
    public const int MaximumSteps = 12;
    public const int MaximumKeys = 8;
    public const int MaximumKeyCharacters = 64;

    // Throws ArgumentException for a malformed batch or unsupported action-specific fields.
    public static IReadOnlyList<CompactUiStep> Read(JsonObject arguments)
    {
        if (arguments["steps"] is not JsonArray steps || steps.Count is < 1 or > MaximumSteps)
        {
            throw new ArgumentException("Supply 1–12 bounded UI steps.");
        }
        var result = new List<CompactUiStep>();
        foreach (JsonObject step in steps.OfType<JsonObject>())
        {
            if (step["action"]?.ToString() is not "click" and not "write" and not "key")
            {
                throw new ArgumentException("Unsupported compact UI action.");
            }
            if (step["action"]?.ToString() == "key")
            {
                result.Add(Keys(step));
                continue;
            }
            string? reference = step["ref"]?.GetValue<string>();
            string? text = step["text"]?.GetValue<string>();
            if (string.IsNullOrEmpty(reference) || step.ContainsKey("keys") || step["action"]?.ToString() == "write"
                && (text == null || text.Length > CompactCaptureCodec.MaximumTextCharacters))
            {
                throw new ArgumentException("Clicks and writes require an observed ref; writes require bounded literal text.");
            }
            if (step["action"]?.ToString() == "click" && step.ContainsKey("text"))
            {
                throw new ArgumentException("Click does not accept text.");
            }
            result.Add(new(step["action"]!.ToString() == "click" ? CompactUiAction.Click : CompactUiAction.Write, reference, text, null));
        }
        if (result.Count != steps.Count)
        {
            throw new ArgumentException("Every UI step must be an object.");
        }

        return result;
    }

    // Throws ArgumentException for missing, oversized or text-carrying keyboard input; actual key validity remains enforced on the node.
    private static CompactUiStep Keys(JsonObject step)
    {
        if (step.ContainsKey("text") || !IsOptionalReference(step) || step["keys"] is not JsonArray keys
            || keys.Count is < 1 or > MaximumKeys || keys.Any(key => key is not JsonValue value
                || !value.TryGetValue<string>(out var text) || text.Length is < 1 or > MaximumKeyCharacters))
        {
            throw new ArgumentException("Keyboard steps require 1–8 bounded key names and no text; a ref focuses its element first.");
        }

        return new(CompactUiAction.Key, step["ref"]?.GetValue<string>(), null, keys.Select(key => key!.GetValue<string>()).ToArray());
    }

    private static bool IsOptionalReference(JsonObject step)
    {
        var node = step["ref"];

        return node == null || node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0;
    }
}
