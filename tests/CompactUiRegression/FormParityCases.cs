using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Platforms;
using KLaniBot.Transport.CompactUi;

// A shared synthetic corpus checks exact form values, revisions, refusals and verified mutation replies in both runtimes.
internal static class FormParityCases
{
    public static async Task<JsonArray> RunAsync(string file)
    {
        var corpus = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        var replies = new JsonArray();
        foreach (JsonNode? fixture in corpus["cases"]!.AsArray())
        {
            var runner = new Runner(fixture!.AsObject());
            replies.Add(await runner.RunAsync());
        }

        return replies;
    }

    // Fixture snapshots stand in for transport responses only; no installed-application or performance claim follows from them.
    private sealed class Runner : ICompactFormBackend
    {
        private readonly JsonObject _fixture;
        private readonly CompactFormRuntime _runtime;
        private readonly CompactFormResponses _responses;
        private readonly Dictionary<string, JsonObject> _replies = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _references = new(StringComparer.Ordinal);
        private CompactFormState? _form;
        private JsonObject _step = new();
        private int _snapshot;
        private int _mutations;

        public Runner(JsonObject fixture)
        {
            _fixture = fixture;
            _runtime = new(this);
            _responses = new(_runtime, new(), new("fixture", Guid.NewGuid()));
        }

        public async Task<JsonObject> RunAsync()
        {
            var steps = new JsonArray();
            foreach (JsonNode? node in _fixture["steps"]!.AsArray())
            {
                _step = node!.AsObject();
                _snapshot = _step["snapshot"]?.GetValue<int>() ?? _snapshot;
                JsonObject response = await CallAsync();
                RegisterReferences();
                _replies[_step["name"]!.GetValue<string>()] = response.DeepClone().AsObject();
                steps.Add(new JsonObject { ["name"] = _step["name"]!.DeepClone(), ["reply"] = Normalize(response) });
            }

            return new()
            {
                ["name"] = _fixture["name"]!.DeepClone(),
                ["steps"] = steps,
                ["mutations"] = _mutations
            };
        }

        private async Task<JsonObject> CallAsync()
        {
            string action = _step["action"]!.GetValue<string>();
            if (action == "read")
            {
                CompactFormSnapshot snapshot = Snapshot();
                CompactSemanticTarget scope = CompactSemanticIdentity.Target(10, snapshot.Nodes, snapshot.Nodes[0],
                    snapshot.Windows.FirstOrDefault());
                string[]? fields = _step["fields"]?.AsArray().Select(field => field!.GetValue<string>()).ToArray();
                var read = await _runtime.ReadAsync(10, scope, fields, CancellationToken.None);
                if (read.Error != null)
                {
                    return Error(read.Error.Value, read.Detail);
                }
                _form = read.Value!;
                CompactFormReply reply = _responses.Read(_form, fields, Page());
                return reply.Value ?? (reply.Error != null ? Error(reply.Error.Value)
                    : new JsonObject { ["error"] = CompactResultErrors.ToWireMessage(reply.EvidenceError!.Value) });
            }
            string context = _step["context_from"] is JsonValue alias
                ? _replies[alias.GetValue<string>()]["context_rev"]!.GetValue<string>() : _form!.ContextRevision;
            if (action == "validate")
            {
                var current = await _runtime.CurrentAsync(_form!.Reference, context, CancellationToken.None);
                return current.Error != null ? Error(current.Error.Value, current.Detail)
                    : CompactFormWire.Validation(_form, CompactFormPolicy.Validate(_form.Fields));
            }

            return await FieldCallAsync(action, context);
        }

        private async Task<JsonObject> FieldCallAsync(string action, string context)
        {
            CompactFormField field = _form!.Fields.Single(field => field.Name == _step["field"]!.GetValue<string>());
            if (action == "options")
            {
                var options = await _runtime.OptionsAsync(_form.Reference, context, field.Reference, Page(), CancellationToken.None);
                return options.Value != null ? CompactFormResponses.Options(options.Value) : Error(options.Error!.Value, options.Detail);
            }
            var value = _step["value"] is JsonValue text ? new CompactFormValue(CompactFormValueKind.Text, Text: text.GetValue<string>())
                : new(CompactFormValueKind.Option, OptionId: _step["option_id"]!.GetValue<string>(),
                    OptionsRevision: _step["options_rev"]?.GetValue<string>() ?? field.OptionsRevision);
            var set = await _runtime.SetAsync(_form.Reference, context, field.Reference, value, CancellationToken.None);

            return set.Value != null ? CompactFormResponses.Set(_form, set.Value) : Error(set.Error!.Value, set.Detail);
        }

        private CompactFormPageOptions Page()
        {
            CompactFormCursor? cursor = _step["cursor_from"] is JsonValue alias
                ? new(Guid.Parse(_replies[alias.GetValue<string>()]["next_cursor"]!.GetValue<string>())) : null;

            return new(cursor, _step["limit"]?.GetValue<int>());
        }

        public Task<CompactFormSnapshot> RefreshAsync(int processId, long windowHandle, CancellationToken cancellation)
        {
            return Task.FromResult(Snapshot());
        }

        public Task<CompactFormDispatch> DispatchAsync(CompactFormMutation mutation, CancellationToken cancellation)
        {
            _mutations++;
            if (_step["after_snapshot"] is JsonValue after)
            {
                _snapshot = after.GetValue<int>();
            }

            return Task.FromResult(new CompactFormDispatch(true));
        }

        private CompactFormSnapshot Snapshot()
        {
            JsonObject snapshot = _fixture["snapshots"]![_snapshot]!.AsObject();
            CompactTreeNode[] nodes = snapshot["nodes"]!.AsArray().Select(node => new CompactTreeNode(
                node!["depth"]!.GetValue<int>(), node["id"]!.GetValue<int>(), CompactRole.Parse(node["role"]!.GetValue<string>()),
                node["name"]!.GetValue<string>(), node["autoId"]!.GetValue<string>(), node["value"]?.GetValue<string>(),
                node["enabled"]!.GetValue<bool>(), node["offscreen"]!.GetValue<bool>())).ToArray();
            var metadata = snapshot["metadata"]!.AsObject().ToDictionary(entry => int.Parse(entry.Key),
                entry => entry.Value!.Deserialize<UiFieldMetadata>()!);
            string started = snapshot["started"]?.GetValue<string>() ?? "start";
            var window = new CompactNativeWindow(10, "fixture", "1.0", nodes[0].Name, 1, started, CompactWindowKind.Main);

            return new(10, "fixture-snapshot", nodes, [window], metadata);
        }

        private void RegisterReferences()
        {
            if (_form == null)
            {
                return;
            }
            _references.TryAdd(_form.Reference.Value.ToString(), "$form");
            foreach (CompactFormField field in _form.Fields)
            {
                _references.TryAdd(field.Reference.Value.ToString(), "$field" + (_references.Count - 1));
            }
        }

        private JsonNode? Normalize(JsonNode? value, string? key = null)
        {
            if (value is JsonObject document)
            {
                return new JsonObject(document.Select(entry => new KeyValuePair<string, JsonNode?>(entry.Key,
                    Normalize(entry.Value, entry.Key))));
            }
            if (value is JsonArray array)
            {
                return new JsonArray(array.Select(item => Normalize(item)).ToArray());
            }
            if (value is JsonValue text && text.TryGetValue<string>(out string? literal))
            {
                string? replacement = key == "next_cursor" ? "$cursor" : key == "evidence_ref" ? "$evidence"
                    : _references.GetValueOrDefault(literal);
                if (key == "section" && literal.StartsWith("outputs/", StringComparison.Ordinal))
                {
                    replacement = "outputs/" + _references[literal["outputs/".Length..]];
                }
                return JsonValue.Create(replacement ?? literal);
            }

            return value?.DeepClone();
        }

        private static JsonObject Error(CompactFormError error, string? detail = null)
        {
            return new()
            {
                ["error"] = detail ?? CompactFormNames.Error(error)
            };
        }
    }
}
