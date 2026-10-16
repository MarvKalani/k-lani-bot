using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Form replies retain explicit unknown constraints and the established field/revision vocabulary at the MCP boundary.
public static class CompactFormWire
{
    public static JsonNode Known(CompactKnownBoolean value)
    {
        return value switch
        {
            CompactKnownBoolean.Unknown => JsonValue.Create("unknown")!,
            CompactKnownBoolean.False => JsonValue.Create(false)!,
            CompactKnownBoolean.True => JsonValue.Create(true)!,
            _ => throw new UnreachableException()
        };
    }

    public static string Kind(CompactFormFieldKind kind)
    {
        return kind switch
        {
            CompactFormFieldKind.Text => "text",
            CompactFormFieldKind.Choice => "choice",
            CompactFormFieldKind.Toggle => "toggle",
            CompactFormFieldKind.Navigation => "navigation",
            CompactFormFieldKind.Action => "action",
            CompactFormFieldKind.Number => "number",
            _ => throw new UnreachableException()
        };
    }

    public static JsonObject Field(CompactFormField field)
    {
        return new()
        {
            ["field_ref"] = field.Reference.Value.ToString(),
            ["name"] = field.Name,
            ["field_key"] = field.Metadata.Field ?? field.Name,
            ["role"] = field.Role.Name,
            ["type"] = Kind(field.Kind),
            ["value"] = field.Value,
            ["visible"] = Known(field.Visible),
            ["enabled"] = field.Enabled,
            ["read_only"] = Known(field.ReadOnly),
            ["required"] = Known(field.Required),
            ["metadata_source"] = field.Metadata.Source.ToString(),
            ["options_rev"] = field.OptionsRevision,
            ["dependencies"] = field.Metadata.Dependencies == null ? JsonValue.Create("unknown")
                : new JsonArray(field.Metadata.Dependencies.Select(dependency => (JsonNode?)JsonValue.Create(dependency)).ToArray())
        };
    }

    public static string StructureFingerprint(IReadOnlyList<CompactFormField> fields)
    {
        var structure = new JsonArray();
        foreach (CompactFormField field in fields)
        {
            structure.Add(new JsonObject
            {
                ["key"] = field.Key,
                ["role"] = field.Role.Name,
                ["visible"] = Known(field.Visible),
                ["enabled"] = field.Enabled,
                ["read_only"] = Known(field.ReadOnly),
                ["required"] = Known(field.Required)
            });
        }

        return CompactFormFields.Fingerprint(structure);
    }

    public static string ContextFingerprint(string structure, string instance, IReadOnlyList<CompactFormField> fields)
    {
        var context = new JsonArray();
        foreach (CompactFormField field in fields)
        {
            var value = new JsonObject { ["key"] = field.Key, ["value"] = field.Value };
            if (field.MetadataPresent)
            {
                value["context"] = field.Metadata.Context;
            }
            value["options"] = field.OptionsRevision;
            context.Add(value);
        }

        return CompactFormFields.Fingerprint(new JsonObject { ["structure"] = structure, ["instance"] = instance, ["fields"] = context });
    }

    public static JsonObject Revisions(CompactFormState form)
    {
        return new()
        {
            ["form_ref"] = form.Reference.Value.ToString(),
            ["context_rev"] = form.ContextRevision,
            ["structure_rev"] = form.StructureRevision
        };
    }

    public static JsonObject Validation(CompactFormState form, CompactFormValidation validation)
    {
        JsonObject result = Revisions(form);
        result["success"] = true;
        result["ready"] = validation.Ready;
        result["missing_required"] = References(validation.MissingRequired);
        result["invalid_values"] = References(validation.InvalidValues);
        result["unknown_constraints"] = References(validation.UnknownConstraints);

        return result;
    }

    public static JsonArray References(IReadOnlyList<CompactFormFieldReference> fields)
    {
        return new(fields.Select(field => (JsonNode?)JsonValue.Create(field.Value.ToString())).ToArray());
    }
}
