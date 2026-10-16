using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Transport;

// Cartridge discovery must load only the selected topic; every pinned recipe still produces a valid core tool request.
internal static class CartridgeTopicCases
{
    private const string BundlePath = "cartridges/programs/outlook-classic/16.0.14334.20918/cartridge.json";

    public static int Run()
    {
        var checks = new Checks("Cartridge topics");
        using var cartridges = new GatewayCartridges(Path.Combine(RepositoryLocator.Root(), BundlePath));
        var available = Enum.GetValues<Tool>().ToHashSet();
        JsonArray routing = CartridgeGatewayTools.Descriptors(false);
        checks.That(routing.Count == 3 && !routing.ToJsonString().Contains("HTMLBody", StringComparison.Ordinal),
            "the permanent MCP catalog contains routing schemas rather than the whole application catalog");
        JsonObject information = routing.OfType<JsonObject>().Single(tool => tool["name"]!.ToString() == "cartridge_info");
        ToolContract.Validate(information["inputSchema"]!.AsObject(), new JsonObject { ["pid"] = 100 });
        checks.That(true, "an explicit process identity validates against the generated schema's integer bounds");
        JsonObject root = cartridges.Browse("", available);
        checks.That(root["topics"]!.AsArray().Count == 4 && !root.ToJsonString().Contains("inputSchema", StringComparison.Ordinal),
            "the compact root contains Administration, Use, Info and Programming without function schemas");
        CheckMailBranch(checks, cartridges, available);
        CheckNativeCapability(checks, cartridges);
        CheckProgramStart(checks, cartridges);
        CheckRecipeRequests(checks, cartridges);

        return checks.Passed;
    }

    private static void CheckMailBranch(Checks checks, GatewayCartridges cartridges, HashSet<Tool> available)
    {
        string reading = cartridges.Browse("Use/Mail/Read", available).ToJsonString();
        checks.That(reading.Contains("outlook_read_selected", StringComparison.Ordinal)
            && !reading.Contains("outlook_set_subject", StringComparison.Ordinal)
            && !reading.Contains("outlook_api", StringComparison.Ordinal),
            "reading mail does not load editing functions or programming metadata");
        checks.That(!reading.Contains("inputSchema", StringComparison.Ordinal),
            "topic discovery returns summaries and a schema requires a selected function");
        available.Remove(Tool.InspectApplication);
        string withoutNativeAccess = cartridges.Browse("Use/Mail/Read", available).ToJsonString();
        checks.That(!withoutNativeAccess.Contains("outlook_read_selected", StringComparison.Ordinal),
            "a native read is not offered when its application access capability is absent");
    }

    private static void CheckNativeCapability(Checks checks, GatewayCartridges cartridges)
    {
        GatewayRecipe identity = cartridges.Recipes.Single(recipe => recipe.Macro.Name == "outlook_identity");
        checks.That(identity.Tools.SequenceEqual(new[] { Tool.InspectApplication }),
            "a native identity query does not require unrelated UI verification capabilities");
        checks.That(cartridges.Recipes.Where(recipe => recipe.Compatibility == CartridgeCompatibility.NativeProbe)
            .All(recipe => recipe.Tools.All(tool => ToolContract.Get(tool)["mutates"]!.GetValue<bool>() == false)),
            "cross-build discovery never grants compatibility to mutating application operations");
    }

    private static void CheckRecipeRequests(Checks checks, GatewayCartridges cartridges)
    {
        foreach (GatewayRecipe recipe in cartridges.Recipes)
        {
            var arguments = new JsonObject();
            foreach (var parameter in recipe.Macro.Parameters.Where(parameter => parameter.Required && parameter.Default == null))
            {
                arguments[parameter.Name] = parameter.Type switch
                {
                    "integer" => JsonValue.Create(parameter.Name == "pid" ? 100 : 1),
                    "boolean" => JsonValue.Create(true),
                    _ => ExampleText(parameter.Name)
                };
            }
            int calls = 0;
            JsonObject result = cartridges.Run(recipe, arguments, frame =>
            {
                Tool tool = ToolNames.Parse(frame["action"]!.ToString());
                ToolContract.Validate(ToolContract.Get(tool)["inputSchema"]!.AsObject(), frame["args"]!.AsObject());
                calls++;
                return new JsonObject { ["success"] = true, ["outcome"] = "completed" };
            });
            checks.That(calls == recipe.Macro.Steps.Count && result["success"]!.GetValue<bool>(),
                recipe.Macro.Name + " expands to valid bounded tool requests");
        }
    }

    private static JsonNode ExampleText(string name)
    {
        string text = name == "expected_content_sha256" ? new string('a', 64)
            : name == "text_format" ? "Readable" : "synthetic";

        return JsonValue.Create(text)!;
    }

    private static void CheckProgramStart(Checks checks, GatewayCartridges cartridges)
    {
        GatewayRecipe start = cartridges.Recipes.Single(recipe => recipe.Macro.Name == "outlook_start");
        checks.That(start.IsProgramStart && start.Macro.Parameters.Count == 0 && start.Tools.SequenceEqual(new[] { Tool.LaunchProcess }),
            "starting the program requires no existing process identity and grants no arbitrary executable parameter");
        string offered = cartridges.Browse("Use/Program", new HashSet<Tool> { Tool.LaunchProcess }).ToJsonString();
        checks.That(offered.Contains("outlook_start", StringComparison.Ordinal),
            "the program start branch is discoverable with process permission before Outlook is running");
        checks.That(cartridges.Browse("Use/Program", new HashSet<Tool>()).ToJsonString()
            .Contains("outlook_start", StringComparison.Ordinal) == false,
            "a cartridge does not invent program-start permission when the node lacks it");
    }
}
