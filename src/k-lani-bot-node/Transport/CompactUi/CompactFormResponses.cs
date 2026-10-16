using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Form response construction preserves full private evidence while exposing only bounded, requested live fields.
public sealed class CompactFormResponses(CompactFormRuntime runtime, CompactResultStore results, CompactResultAuthority authority)
{
    public const int MaximumValueCharacters = 2000;

    public CompactFormReply Read(CompactFormState form, IReadOnlyList<string>? requested, CompactFormPageOptions options)
    {
        if (requested != null && requested.Any(name => form.Fields.Count(field => CompactFormFields.Matches(field, name)) != 1))
        {
            return new(null, CompactFormError.RequestedFieldUnknownOrAmbiguous);
        }
        CompactFormField[] selected = form.Fields.Where(field => requested == null
            || requested.Any(name => CompactFormFields.Matches(field, name))).ToArray();
        JsonObject[] records = selected.Select(CompactFormWire.Field).ToArray();
        CompactResultError? error = RetainLongValues(selected, records);
        if (error != null)
        {
            return new(null, EvidenceError: error);
        }
        if (!runtime.TryFieldPage(form, requested, options, out var page))
        {
            return new(null, page.Error);
        }
        var byReference = records.ToDictionary(record => record["field_ref"]!.GetValue<string>(), StringComparer.Ordinal);
        JsonObject reply = CompactFormWire.Revisions(form);
        reply["success"] = true;
        reply["fields"] = new JsonArray(page.Value!.Items.Select(field => (JsonNode?)
            byReference[field.Reference.Value.ToString()]).ToArray());
        reply["complete"] = page.Value.Complete;
        reply["next_cursor"] = page.Value.NextCursor?.Value.ToString();
        reply["changed"] = CompactFormWire.References(form.Changed);
        reply["removed"] = CompactFormWire.References(form.Removed);

        return new(reply);
    }

    private CompactResultError? RetainLongValues(CompactFormField[] fields, JsonObject[] records)
    {
        var values = fields.Where(field => field.Value != null && CompactUnicode.Count(field.Value) > MaximumValueCharacters)
            .ToDictionary(field => field.Reference.Value.ToString(), field => field.Value!, StringComparer.Ordinal);
        if (values.Count == 0)
        {
            return null;
        }
        var debug = new JsonArray(records.Select(record => record.DeepClone()).ToArray());
        if (!results.TryPut(authority, values, CompactJsonText.Serialize(debug), out var stored))
        {
            return stored.Error;
        }
        foreach (JsonObject record in records)
        {
            string reference = record["field_ref"]!.GetValue<string>();
            if (values.TryGetValue(reference, out string? value))
            {
                record["value"] = CompactUnicode.Slice(value, 0, MaximumValueCharacters);
                record["value_truncated"] = true;
                record["evidence_ref"] = stored.Value!.Value.ToString();
                record["section"] = "outputs/" + reference;
            }
        }

        return null;
    }

    public static JsonObject Options(CompactFormOptionsResult result)
    {
        if (!result.SelectionPreserved)
        {
            return new()
            {
                ["success"] = false,
                ["status"] = "unconfirmed",
                ["opened"] = true,
                ["error"] = result.Error
            };
        }
        JsonObject reply = CompactFormWire.Revisions(result.Form);
        reply["success"] = true;
        reply["field_ref"] = result.Field.Reference.Value.ToString();
        reply["options_rev"] = result.Field.OptionsRevision;
        reply["options"] = new JsonArray(result.Page.Items.Select(option => (JsonNode?)new JsonObject
        {
            ["option_id"] = option.Id,
            ["label"] = option.Label,
            ["enabled"] = option.Enabled
        }).ToArray());
        reply["complete"] = result.Page.Complete && result.NativeComplete;
        reply["next_cursor"] = result.Page.NextCursor?.Value.ToString();
        reply["native_complete"] = result.NativeComplete;
        reply["ui_steps"] = result.Opened ? new JsonArray(new JsonObject
        {
            ["action"] = "open_dropdown",
            ["field_ref"] = result.Field.Reference.Value.ToString(),
            ["selection_performed"] = false
        }) : new JsonArray();

        return reply;
    }

    public static JsonObject Set(CompactFormState form, CompactFormSetResult result)
    {
        return Set(form.Reference, result);
    }

    public static JsonObject Set(CompactFormReference reference, CompactFormSetResult result)
    {
        var reply = new JsonObject
        {
            ["form_ref"] = reference.Value.ToString(),
            ["context_rev"] = result.ContextRevision,
            ["structure_rev"] = result.StructureRevision,
            ["success"] = result.Success,
            ["verified"] = result.Verified
        };
        if (!result.Success)
        {
            reply["status"] = "unconfirmed";
            reply["input_sent"] = "possibly";
            reply["error"] = result.Error;
            reply["retry"] = "Observe current state; never automatically replay this mutation.";
            return reply;
        }
        reply["field_ref"] = result.Field.Value.ToString();
        reply["invalidated_fields"] = CompactFormWire.References(result.InvalidatedFields);
        reply["dependency_evidence"] = new JsonObject
        {
            ["source"] = result.MetadataSource.ToString(),
            ["status"] = result.DependenciesKnown ? "provider_declared" : "unknown",
            ["checked_context"] = result.CheckedContext,
            ["temporal_inference"] = false
        };

        return reply;
    }
}

// Protocol response failures distinguish form state refusal from a private evidence-store budget failure.
public sealed record CompactFormReply(JsonObject? Value, CompactFormError? Error = null, CompactResultError? EvidenceError = null);
