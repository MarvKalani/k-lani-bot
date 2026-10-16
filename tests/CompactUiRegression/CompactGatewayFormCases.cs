using System.Text.Json.Nodes;
using KLaniBot.Transport.CompactUi;

// Routed form regressions use explicitly synthetic transport state; the fixture owns the independent mutation oracle.
internal static class CompactGatewayFormCases
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "k-lani-form-routing-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            await DiscoveryAsync(root);
            await LifecycleAsync(root);
            await InvalidInputsAsync(root);
            await FreshStateAsync(root);
            await FailedWritesAsync(root);
            await OptionsAsync(root);
            await LongValuesAsync(root);
            await OpeningAsync(root);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static CompactGatewaySession Session(NativeAdapterFixture client, string root)
    {
        return new(client, "fixture", new(root, null), () => "2025-11-25");
    }

    private static async Task<JsonObject> ReadAsync(CompactGatewaySession session, JsonArray? fields = null)
    {
        await session.CallAsync("ui_info", new(), CancellationToken.None);
        var view = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = 10, ["full"] = true }, CancellationToken.None);
        var scope = view["elements"]!.AsArray().OfType<JsonObject>().Single(element => element["role"]!.ToString() == "Window");
        var arguments = new JsonObject { ["pid"] = 10, ["scope"] = scope["ref"]!.ToString() };
        if (fields != null)
        {
            arguments["fields"] = fields;
        }

        return await CallAsync(session, "form.read", arguments);
    }

    private static Task<JsonObject> CallAsync(CompactGatewaySession session, string function, JsonObject arguments)
    {
        return session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = function,
            ["arguments"] = arguments
        }, CancellationToken.None);
    }

    private static JsonObject FieldArguments(JsonObject form, string name)
    {
        var field = Field(form, name);
        return new JsonObject
        {
            ["form_ref"] = form["form_ref"]!.ToString(),
            ["context_rev"] = form["context_rev"]!.ToString(),
            ["field_ref"] = field["field_ref"]!.ToString()
        };
    }

    private static JsonObject Field(JsonObject form, string name)
    {
        return form["fields"]!.AsArray().OfType<JsonObject>().Single(field => field["name"]!.ToString() == name);
    }

    private static async Task DiscoveryAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true, Build = "unknown" };
        using var session = Session(client, root);
        var form = await ReadAsync(session);
        Require(form["success"]?.ToString() == "true", "live form does not require an invented known build");
        var branch = await session.CallAsync("cartridge_browse", new JsonObject { ["topic"] = "Use/Forms" }, CancellationToken.None);
        var functions = branch["functions"]!.AsArray().OfType<JsonObject>().ToArray();
        Require(functions.Length == 4 && functions.All(function => function["inputSchema"] == null),
            "only bounded form names are discovered even with an unknown build");
        var descriptor = await session.CallAsync("cartridge_browse", new JsonObject { ["function"] = "form.set" }, CancellationToken.None);
        Require(descriptor["function"]!["inputSchema"] != null && client.Mutations.Count == 0,
            "selected schema discovery performs no input");
    }

    private static async Task LifecycleAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = Session(client, root);
        var unseen = await CallAsync(session, "form.read", new JsonObject { ["pid"] = 10, ["scope"] = "e1" });
        Require(unseen["success"]?.ToString() == "false" && client.Mutations.Count == 0, "Info required before form access");
        var form = await ReadAsync(session, new("Field"));
        Require(form["success"]?.ToString() == "true" && form["fields"]!.AsArray().Count == 1, "requested field scope only");
        var arguments = FieldArguments(form, "Field");
        arguments["value"] = "replacement";
        var set = await CallAsync(session, "form.set", arguments);
        Require(set["verified"]?.ToString() == "true" && client.Field == "replacement", "complete replacement oracle");
        var validation = await CallAsync(session, "form.validate", new JsonObject
        {
            ["form_ref"] = set["form_ref"]!.ToString(),
            ["context_rev"] = set["context_rev"]!.ToString()
        });
        Require(validation["success"]?.ToString() == "true" && validation["ready"]?.ToString() == "false",
            "unknown constraints remain not ready");
        var current = await ReadAsync(session, new("Field"));
        var clear = FieldArguments(current, "Field");
        clear["value"] = "";
        var cleared = await CallAsync(session, "form.set", clear);
        Require(cleared["verified"]?.ToString() == "true" && client.Field == "", "explicit clear is verified");
    }

    private static async Task InvalidInputsAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = Session(client, root);
        var form = await ReadAsync(session);
        var invalid = FieldArguments(form, "Field");
        invalid["value"] = "never";
        invalid["option_id"] = "a";
        invalid["options_rev"] = "choice-revision";
        var both = await CallAsync(session, "form.set", invalid);
        Require(both["success"]?.ToString() == "false", "literal and option XOR");
        var duplicate = await ReadAsync(session, new("Field", "Field"));
        Require(duplicate["success"]?.ToString() == "false", "duplicate requested fields refused");
        var forged = FieldArguments(form, "Field");
        forged["field_ref"] = Guid.NewGuid().ToString();
        forged["value"] = "never";
        var unknown = await CallAsync(session, "form.set", forged);
        Require(unknown["success"]?.ToString() == "false", "unknown field refused");
        using var other = Session(client, root);
        await other.CallAsync("ui_info", new(), CancellationToken.None);
        var foreign = FieldArguments(form, "Field");
        foreign["value"] = "never";
        var crossSession = await CallAsync(other, "form.set", foreign);
        Require(crossSession["success"]?.ToString() == "false" && client.Mutations.Count == 0 && client.Field == "old",
            "foreign session and malformed requests cannot mutate");
    }

    private static async Task FreshStateAsync(string root)
    {
        foreach (var change in Enum.GetValues<FormRoutingChange>())
        {
            var client = new NativeAdapterFixture { Dialog = true };
            using var session = Session(client, root);
            var form = await ReadAsync(session);
            ApplyChange(client, change);
            var arguments = FieldArguments(form, "Field");
            arguments["value"] = "never";
            var refused = await CallAsync(session, "form.set", arguments);
            Require(refused["success"]?.ToString() == "false" && client.Mutations.Count == 0,
                "fresh " + change + " refuses before input");
        }
    }

    private static void ApplyChange(NativeAdapterFixture client, FormRoutingChange change)
    {
        switch (change)
        {
            case FormRoutingChange.Value:
                client.ForceField("external edit");
                break;
            case FormRoutingChange.ReadOnly:
                client.ReadOnly = true;
                break;
            case FormRoutingChange.Disabled:
                client.DisabledField = true;
                break;
            case FormRoutingChange.Role:
                client.WrongRole = true;
                break;
            case FormRoutingChange.Instance:
                client.Started = "2026-10-09T00:00:00Z";
                break;
            default:
                throw new System.Diagnostics.UnreachableException();
        }
    }

    private static async Task FailedWritesAsync(string root)
    {
        foreach (var fault in new[] { NativeAdapterFault.Append, NativeAdapterFault.Empty, NativeAdapterFault.WrongTail,
            NativeAdapterFault.LostResponse, NativeAdapterFault.MissingOutcome })
        {
            var client = new NativeAdapterFixture { Dialog = true, Fault = fault };
            using var session = Session(client, root);
            var form = await ReadAsync(session);
            var arguments = FieldArguments(form, "Field");
            arguments["value"] = fault == NativeAdapterFault.WrongTail ? new string('x', 16000) : "replacement";
            var set = await CallAsync(session, "form.set", arguments);
            Require(set["success"]?.ToString() == "false" && set["verified"]?.ToString() == "false"
                && set["status"]?.ToString() == "unconfirmed" && client.Mutations.Count == 1,
                "full mismatch or missing native confirmation never retries " + fault);
        }
    }

    private static async Task OptionsAsync(string root)
    {
        var client = new NativeAdapterFixture { Dialog = true };
        using var session = Session(client, root);
        var form = await ReadAsync(session);
        var arguments = FieldArguments(form, "Choice");
        arguments["limit"] = 1;
        var first = await CallAsync(session, "form.options", arguments);
        Require(first["complete"]?.ToString() == "false" && client.SelectedChoice == "a" && client.Mutations.Count == 0,
            "bounded option page does not select");
        arguments = FieldArguments(form, "Choice");
        arguments["limit"] = 1;
        arguments["cursor"] = first["next_cursor"]!.ToString();
        var second = await CallAsync(session, "form.options", arguments);
        Require(second["complete"]?.ToString() == "true" && second["options"]![0]!["option_id"]!.ToString() == "b",
            "option cursor retains full scope");
        var select = FieldArguments(form, "Choice");
        select["option_id"] = "b";
        select["options_rev"] = "stale";
        var stale = await CallAsync(session, "form.set", select.DeepClone().AsObject());
        Require(stale["success"]?.ToString() == "false" && client.Mutations.Count == 0, "stale option revision refused");
        select["options_rev"] = second["options_rev"]!.ToString();
        var selected = await CallAsync(session, "form.set", select);
        Require(selected["verified"]?.ToString() == "true" && client.SelectedChoice == "b" && client.Mutations.Count == 1,
            "real option ID verified by independent selection state");
    }

    private static async Task OpeningAsync(string root)
    {
        foreach (bool changesSelection in new[] { false, true })
        {
            var client = new NativeAdapterFixture
            {
                Dialog = true,
                ChoicesNeedOpen = true,
                OpeningChangesSelection = changesSelection
            };
            using var session = Session(client, root);
            var form = await ReadAsync(session);
            var options = await CallAsync(session, "form.options", FieldArguments(form, "Choice"));
            Require(client.Mutations.Count == 1, "dropdown opening is a single actual mutation");
            if (changesSelection)
            {
                Require(options["status"]?.ToString() == "unconfirmed" && options["success"]?.ToString() == "false",
                    "opening cannot silently change selection or retry");
            }
            else
            {
                Require(options["success"]?.ToString() == "true" && client.SelectedChoice == "a"
                    && options["ui_steps"]![0]!["selection_performed"]?.ToString() == "false",
                    "native dropdown opening preserves selection and is reported");
            }
        }
    }

    private static async Task LongValuesAsync(string root)
    {
        string text = new string('x', 15999) + "z";
        var client = new NativeAdapterFixture { Dialog = true };
        client.ForceField(text);
        using var session = Session(client, root);
        var form = await ReadAsync(session, new("Field"));
        var field = Field(form, "Field");
        Require(field["value_truncated"]?.ToString() == "true" && field["value"]!.ToString().Length == 2000,
            "long reply explicitly clipped");
        var tail = await CallAsync(session, "Info.result", new JsonObject
        {
            ["evidence_ref"] = field["evidence_ref"]!.ToString(),
            ["section"] = field["section"]!.ToString(),
            ["offset"] = 15999,
            ["limit"] = 2000
        });
        Require(tail["text"]?.ToString() == "z", "full long field remains available by scoped evidence");
    }

    private static void Require(bool condition, string name)
    {
        WorkflowEngineSyntheticAdapter.Require(condition, name);
    }
}

// Each case changes one independently owned current property before a routed form mutation.
internal enum FormRoutingChange
{
    Value,
    ReadOnly,
    Disabled,
    Role,
    Instance
}
