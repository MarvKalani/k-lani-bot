using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Transport.CompactUi;

// Native adapter tests use real private library/job files and explicitly synthetic transport/application state, never provider credentials.
internal sealed class NativeAdapterEnvironment : IDisposable
{
    public static readonly CompactProgramIdentity Application = new("fixture-editor", "1.0");
    public static readonly CompactContractDigest Gateway = new(new string('b', 64));
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "k-lani-native-adapter-" + Guid.NewGuid());
    public NativeAdapterFixture Client { get; } = new();
    public CompactNodeAccess Node
    {
        get;
    }
    public CompactFunctionExecutor Executor
    {
        get;
    }
    public CompactFunctionLibrary Library
    {
        get;
    }
    public CompactWorkflowFunctionCatalog Catalog
    {
        get;
    }

    public NativeAdapterEnvironment()
    {
        Node = new(Client, "fixture-node");
        Executor = new(Node, CompactCompatibilityWire.Wire("2025-06-18"), Gateway);
        string file = Path.Combine(Root, "exports", "generated-ui.json");
        Require(CompactFunctionLibrary.TryOpen(file, Path.Combine(Root, "recovery"), Application, out var library), "open active library");
        Library = library.Value!;
        Catalog = new(new Dictionary<CompactProgramIdentity, CompactWorkflowProgram> { [Application] = new("fixture-editor", Library) });
    }

    public CompactLoadedFunction Install(string name, IReadOnlyList<CompactRecipeStep> steps, IReadOnlyList<CompactRecipeOutput> outputs)
    {
        var draft = new CompactFunctionDraft(name, new(CompactTopicCategory.Use, "Editor"), steps, outputs,
            steps.All(step => step.Action == CompactRecipeAction.Read) ? CompactCaptureValidation.ObservedRead
                : CompactCaptureValidation.DemonstratedMutation);

        return InstallDraft(draft);
    }

    // Installs a draft exactly as the production compiler produced it; only the synthetic node compatibility proof is fixture-owned.
    public CompactLoadedFunction InstallDraft(CompactFunctionDraft draft)
    {
        Require(CompactContractHash.TrySchemas(ToolContract.Tools, out var schemas), "actual embedded test schemas");
        var current = new CompactObservedCompatibility("synthetic-node-1", CompactCompatibilityWire.Wire("2025-06-18"),
            "linux-x11-atspi", Application, schemas.Value!, new(new string('a', 64)), Gateway);
        Require(CompactCompatibility.TryCapture(draft, current, out var compatibility), "synthetic observed compatibility");
        Require(Library.TryInstall(draft, compatibility.Value!, out _), "install active function");
        Require(Library.TryGet(draft.Name, out var loaded), "load installed function");

        return loaded.Value!;
    }

    public CompactLoadedFunction Write(bool commit = false, CompactRecipeRoute route = CompactRecipeRoute.NativeText,
        CompactWindowKind windowKind = CompactWindowKind.Main)
    {
        var steps = new List<CompactRecipeStep>
        {
            new(CompactRecipeAction.Write, Selector("Edit", "Field", "field", windowKind), route, "text")
        };
        if (commit)
        {
            steps.Add(new(CompactRecipeAction.Click, Selector("Button", "Commit", "commit"), CompactRecipeRoute.Semantic));
        }
        CompactRecipeOutput output = commit ? new("value", Selector("Text", "Receipt", "receipt", windowKind),
            CompactOutputField.Value, CompactOutputCheckKind.Changed) : new("value", Selector("Edit", "Field", "field", windowKind),
            CompactOutputField.Value, CompactOutputCheckKind.Exists);

        return Install("write", steps, [output]);
    }

    public void Open()
    {
        Install("open", [new(CompactRecipeAction.Click, Selector("Button", "Open", "open"), CompactRecipeRoute.Semantic)],
            [new("shown", Selector("Edit", "Field", "field"), CompactOutputField.Name, CompactOutputCheckKind.Exists)]);
    }

    public CompactWorkflowEngine Workflow(JsonObject source, out CompactWorkflowStore store)
    {
        Require(CompactWorkflowDefinitionCodec.TryRead(source, Catalog, out var definition), "native workflow definition");
        var authority = new CompactAcceptanceAuthority("fixture-node", "owner", "fixture-client");
        Require(CompactWorkflowStore.TryOpen(Path.Combine(Root, "jobs"), Path.Combine(Root, "exports"), authority, Catalog,
            out var opened), "actual private workflow store");
        store = opened.Value!;
        var adapter = new CompactNativeWorkflowAdapter(Node, Executor, Catalog);

        return new(store, adapter, Catalog, new Dictionary<string, CompactWorkflowDefinition> { ["fixture"] = definition.Value! });
    }

    public static JsonObject Definition(bool open = false)
    {
        var stage = new JsonObject
        {
            ["id"] = "write",
            ["function_ref"] = new JsonObject { ["application"] = "editor", ["name"] = "write" },
            ["bindings"] = new JsonObject { ["text"] = new JsonObject { ["input"] = "text" } },
            ["preconditions"] = "fresh_unique_enabled_targets",
            ["postconditions"] = "recorded_function_outputs"
        };
        var stages = new JsonArray();
        if (open)
        {
            var first = stage.DeepClone().AsObject();
            first["id"] = "open";
            first["function_ref"]!["name"] = "open";
            first["bindings"] = new JsonObject();
            stages.Add(first);
        }
        stages.Add(stage);

        return new JsonObject
        {
            ["format"] = "k-lani-bot-workflow/1",
            ["key"] = "fixture",
            ["revision"] = 1,
            ["inputs"] = new JsonObject { ["text"] = new JsonObject { ["type"] = "string" } },
            ["applications"] = new JsonObject
            {
                ["editor"] = new JsonObject
                {
                    ["key"] = Application.Key,
                    ["version"] = Application.Build,
                    ["window"] = "main"
                }
            },
            ["stages"] = stages,
            ["outputs"] = new JsonObject { ["result"] = new JsonObject { ["stage"] = "write", ["output"] = "value" } }
        };
    }

    public static CompactStoredSelector Selector(string role, string name, string autoId,
        CompactWindowKind windowKind = CompactWindowKind.Main)
    {
        return new(CompactRole.Parse(role), name, autoId, windowKind, []);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, true);
        }
    }

    private static void Require(bool condition, string name)
    {
        WorkflowEngineSyntheticAdapter.Require(condition, name);
    }
}
