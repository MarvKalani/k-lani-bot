using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KLaniBot.Transport.CompactUi;

// The capture boundary validates names, finite output fields and complete write parameterization before compiling a recipe.
public static class CompactCaptureCodec
{
    public const int MaximumSteps = 12;
    public const int MaximumOutputs = 8;
    public const int MaximumTextCharacters = 16000;
    private static readonly Regex TopicPattern = new(@"\A(Administration|Use|Info|Programming)/[A-Za-z0-9._/-]{1,100}\z",
        RegexOptions.CultureInvariant);
    public const int MaximumDescriptionCharacters = 200;
    private static readonly string[] Arguments = ["evidence", "name", "topic", "description", "parameters", "outputs"];

    // Learning reads the evidence token only when it names the current demonstration; a missing, stale or absent token is one refusal.
    public static bool HasCurrentEvidence(JsonNode? arguments, CompactDemonstration? demonstration)
    {
        return arguments is JsonObject document && demonstration != null
            && CompactDiscoveryDocumentCodec.Text(document, "evidence", out string evidence)
            && evidence == demonstration.Evidence.ToString();
    }

    public static bool TryRequest(JsonNode? arguments, CompactDemonstration? demonstration,
        out CompactCaptureAttempt<CompactCaptureRequest> attempt)
    {
        attempt = new(null, CompactCaptureError.EvidenceMissingOrUnknown);
        if (arguments is not JsonObject document || demonstration == null || !HasCurrentEvidence(arguments, demonstration))
        {
            return false;
        }
        if (!CompactDiscoveryDocumentCodec.Text(document, "name", out string name) || !CompactDiscoveryCodec.ValidName(name)
            || !CompactDiscoveryDocumentCodec.Text(document, "topic", out string topic) || !TryTopic(topic, out var selected))
        {
            attempt = new(null, CompactCaptureError.InvalidNameOrTopic);
            return false;
        }
        if (document.Any(entry => !Arguments.Contains(entry.Key, StringComparer.Ordinal)))
        {
            attempt = new(null, CompactCaptureError.UnknownArgument);
            return false;
        }
        if (!TryDescription(document, out string? description))
        {
            attempt = new(null, CompactCaptureError.InvalidDescription);
            return false;
        }
        if (!TryParameters(document["parameters"], demonstration, out var parameters))
        {
            attempt = new(null, CompactCaptureError.InvalidParameters);
            return false;
        }

        if (!TryOutputs(document, demonstration, name, selected!, parameters!, out attempt))
        {
            return false;
        }
        attempt = new(attempt.Value! with
        {
            Description = description
        });

        return true;
    }

    // The description is optional; when present it must be valid, and a stored learning document follows the same rule.
    public static bool TryDescription(JsonObject document, out string? description)
    {
        description = null;
        if (!document.ContainsKey("description"))
        {
            return true;
        }
        if (!CompactDiscoveryDocumentCodec.Text(document, "description", out string text) || !ValidDescription(text))
        {
            return false;
        }
        description = text;

        return true;
    }

    // A description is one short line a later model reads in ui_info; a control character could break that line or hide text.
    private static bool ValidDescription(string description)
    {
        return description.Length is >= 1 and <= MaximumDescriptionCharacters && !description.Any(char.IsControl);
    }

    private static bool TryOutputs(JsonObject document, CompactDemonstration demonstration, string name,
        CompactCartridgeTopic topic, IReadOnlyList<string> parameters, out CompactCaptureAttempt<CompactCaptureRequest> attempt)
    {
        attempt = new(null, CompactCaptureError.UnsupportedSequence);
        if (demonstration.Steps.Any(step => step.Action is not CompactRecipeAction.Click and not CompactRecipeAction.Write))
        {
            return false;
        }
        if (document["outputs"] is not JsonArray outputs || outputs.Count is < 1 or > MaximumOutputs)
        {
            attempt = new(null, CompactCaptureError.InvalidOutputs);
            return false;
        }
        var records = new List<CompactCaptureOutput>();
        foreach (JsonNode? node in outputs)
        {
            if (!TryOutput(node, records.Select(output => output.Name), out var output))
            {
                attempt = new(null, output.Error);
                return false;
            }
            records.Add(output.Value!);
        }
        attempt = new(new(name, topic, parameters, records));

        return true;
    }

    private static bool TryOutput(JsonNode? node, IEnumerable<string> names, out CompactCaptureAttempt<CompactCaptureOutput> attempt)
    {
        attempt = new(null, CompactCaptureError.InvalidOutput);
        if (node is not JsonObject output || !CompactDiscoveryDocumentCodec.Only(output, "name", "ref", "field", "name_prefix")
            || !CompactDiscoveryDocumentCodec.Text(output, "name", out string name) || !CompactDiscoveryCodec.ValidName(name)
            || names.Contains(name, StringComparer.Ordinal) || !CompactDiscoveryDocumentCodec.Text(output, "ref", out string reference)
            || !CompactDiscoveryDocumentCodec.Text(output, "field", out string field) || !TryField(field, out var selected))
        {
            return false;
        }
        string? prefix = null;
        if (output.ContainsKey("name_prefix"))
        {
            if (!CompactDiscoveryDocumentCodec.Text(output, "name_prefix", out string text) || text.Length is < 4 or > 256)
            {
                attempt = new(null, CompactCaptureError.InvalidPrefix);
                return false;
            }
            prefix = text;
        }
        attempt = new(new(name, reference, selected, prefix));

        return true;
    }

    private static bool TryParameters(JsonNode? node, CompactDemonstration demonstration, out IReadOnlyList<string>? parameters)
    {
        parameters = null;
        if (node is not JsonArray values || values.Count != demonstration.Steps.Count(step => step.Action == CompactRecipeAction.Write))
        {
            return false;
        }
        var selected = new List<string>();
        foreach (JsonNode? value in values)
        {
            if (value is not JsonValue text || !text.TryGetValue(out string? name) || !CompactDiscoveryCodec.ValidName(name)
                || selected.Contains(name!, StringComparer.Ordinal))
            {
                return false;
            }
            selected.Add(name!);
        }
        parameters = selected;

        return true;
    }

    public static bool TryTopic(string value, out CompactCartridgeTopic? topic)
    {
        topic = null;
        if (value == null || !TopicPattern.IsMatch(value))
        {
            return false;
        }
        int separator = value.IndexOf('/');
        if (!Enum.TryParse(value[..separator], false, out CompactTopicCategory category) || !Enum.IsDefined(category))
        {
            return false;
        }
        topic = new(category, value[(separator + 1)..]);

        return true;
    }

    public static bool TryField(string value, out CompactOutputField field)
    {
        field = CompactOutputField.Name;
        if (value == "name")
        {
            return true;
        }
        if (value == "value")
        {
            field = CompactOutputField.Value;
            return true;
        }

        return false;
    }
}
