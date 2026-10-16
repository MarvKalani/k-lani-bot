using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Discovery wire records keep inventory and live contents separate; only the explicitly structural representation is persisted.
public static class CompactDiscoveryWire
{
    public const string Format = "k-lani-bot-observed-forms";
    public const int Version = 1;

    public static JsonObject Selector(CompactStoredSelector selector)
    {
        var result = new JsonObject { ["role"] = selector.Role.Name };
        if (selector.Name != null)
        {
            result["name"] = selector.Name;
        }
        if (selector.AutoId != null)
        {
            result["autoId"] = selector.AutoId;
        }
        result["window"] = CompactWindowKindWords.Text(selector.WindowKind);
        if (selector.ContainerPath != null)
        {
            result["containerPath"] = ContainerPath(selector.ContainerPath);
        }
        if (selector.NamePrefix != null)
        {
            result["namePrefix"] = selector.NamePrefix;
        }

        return result;
    }

    // The container path is the same wire shape for stored selectors and automatic names, so both read it from here.
    public static JsonArray ContainerPath(IReadOnlyList<CompactContainerSelector> containers)
    {
        return new JsonArray(containers.Select(container => (JsonNode?)new JsonObject
        {
            ["role"] = container.Role.Name,
            [container.AutoId != null ? "autoId" : "name"] = container.AutoId ?? container.Name
        }).ToArray());
    }

    public static JsonObject Field(CompactObservedField field, bool stored)
    {
        var result = new JsonObject
        {
            ["key"] = field.Key,
            ["name"] = field.Name,
            ["name_truncated"] = field.NameTruncated,
            ["type"] = CompactFormWire.Kind(field.Kind),
            ["role"] = field.Role.Name,
            ["enabled"] = field.Enabled,
            ["offscreen"] = field.Offscreen,
            ["target_ready"] = field.TargetReady,
            ["actions"] = Strings(CompactDiscoveryFields.Actions(field.Kind).Select(Action))
        };
        if (stored)
        {
            result["selector"] = Selector(field.Selector);
        }

        return result;
    }

    public static string Action(CompactSuggestedAction action)
    {
        return action switch
        {
            CompactSuggestedAction.Read => "read",
            CompactSuggestedAction.SetText => "set_text",
            CompactSuggestedAction.Select => "select",
            CompactSuggestedAction.Toggle => "toggle",
            CompactSuggestedAction.Invoke => "invoke",
            CompactSuggestedAction.SetValue => "set_value",
            _ => throw new UnreachableException()
        };
    }

    public static JsonObject Compatibility(CompactScanCompatibility compatibility)
    {
        return new()
        {
            ["node_version"] = compatibility.NodeVersion,
            ["platform"] = compatibility.Platform,
            ["application_version"] = compatibility.ApplicationVersion
        };
    }

    public static JsonObject Scan(CompactScanResult result)
    {
        CompactScanObservation observation = result.Observation;
        var response = new JsonObject
        {
            ["form"] = observation.Form,
            ["window"] = observation.Window.Handle,
            ["kind"] = CompactWindowKindWords.Text(observation.Window.Kind),
            ["state"] = observation.State,
            ["fields"] = new JsonArray(observation.Fields.Select(field => (JsonNode?)Field(field, false)).ToArray()),
            ["unknown"] = Strings(CompactDiscoveryNames.Unknown),
            ["action_status"] = CompactDiscoveryNames.ActionStatus,
            ["offscreen_meaning"] = CompactDiscoveryNames.OffscreenMeaning,
            ["coverage"] = CompactDiscoveryNames.Coverage
        };
        if (observation.Contents != null)
        {
            response["contents"] = Contents(observation.Contents);
        }
        response["observed_states"] = result.ObservedStates;
        response["change"] = new JsonObject
        {
            ["added"] = Strings(result.Changes.Added),
            ["removed"] = Strings(result.Changes.Removed),
            ["changed"] = Strings(result.Changes.Changed)
        };

        return response;
    }

    private static JsonObject Contents(CompactScanContents contents)
    {
        return new()
        {
            ["fields"] = new JsonArray(contents.Fields.Select(field => (JsonNode?)new JsonObject
            {
                ["key"] = field.Key,
                ["name"] = field.Name,
                ["value"] = field.Value,
                ["value_truncated"] = field.Truncated
            }).ToArray()),
            ["texts"] = Strings(contents.Texts),
            ["text_limit"] = CompactFormRuntime.MaximumFields,
            ["text_truncated"] = contents.TextTruncated
        };
    }

    public static JsonObject Read(string name, string state, CompactObservedForm form)
    {
        CompactObservedState selected = form.States[state];

        return new()
        {
            ["form"] = name,
            ["state"] = state,
            ["observed_states"] = form.States.Count,
            ["fields"] = new JsonArray(selected.Fields.Select(field => (JsonNode?)Field(field, false)).ToArray()),
            ["compatibility"] = Compatibility(selected.Compatibility),
            ["observed_transitions"] = Transitions(form.Transitions),
            ["status"] = CompactDiscoveryNames.StoredStatus,
            ["unknown"] = Strings(CompactDiscoveryNames.Unknown)
        };
    }

    public static JsonObject Document(CompactProgramIdentity application, IReadOnlyDictionary<string, CompactObservedForm> forms)
    {
        var records = new JsonObject();
        foreach (KeyValuePair<string, CompactObservedForm> form in forms)
        {
            var states = new JsonObject();
            foreach (KeyValuePair<string, CompactObservedState> state in form.Value.States)
            {
                states[state.Key] = new JsonObject
                {
                    ["fields"] = new JsonArray(state.Value.Fields.Select(field => (JsonNode?)Field(field, true)).ToArray()),
                    ["compatibility"] = Compatibility(state.Value.Compatibility)
                };
            }
            records[form.Key] = new JsonObject
            {
                ["latest"] = form.Value.Latest,
                ["states"] = states,
                ["observed_transitions"] = Transitions(form.Value.Transitions)
            };
        }

        return new()
        {
            ["format"] = Format,
            ["version"] = Version,
            ["application"] = new JsonObject { ["key"] = application.Key, ["version"] = application.Build },
            ["forms"] = records
        };
    }

    private static JsonArray Transitions(IReadOnlyList<CompactObservedTransition> transitions)
    {
        return new(transitions.Select(transition => (JsonNode?)new JsonObject
        {
            ["from"] = transition.From,
            ["to"] = transition.To,
            ["cause"] = CompactDiscoveryNames.TransitionCause
        }).ToArray());
    }

    private static JsonArray Strings(IEnumerable<string> values)
    {
        return new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    }
}
