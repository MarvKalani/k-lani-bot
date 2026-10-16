using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Actions.Handlers;
using KLaniBot.Advisory;
using KLaniBot.Audit;
using KLaniBot.Guard;
using KLaniBot.Platforms;
using KLaniBot.Transport;

// The node's core path in one scenario: credentials, workspace files, element selection, operations, macros, desktop
// sessions, evaluation, classifier and revocation. The steps share one dispatcher, policy file and fake desktop and run
// in this order, because later steps rely on what earlier ones left behind.
internal sealed class CoreScenarioCases
{
    private readonly string temp;
    private readonly string workspace;
    private readonly string state;
    private readonly string token = new('t', 40);
    private readonly string file;
    private NodePolicy policy;
    private readonly FakeUi ui = new();
    private readonly ActionDispatcher dispatcher;
    private readonly RequestContext context = new("test", new string('a', 32));
    private JsonObject click = new();
    private int count;

    private CoreScenarioCases(string temp)
    {
        this.temp = temp;
        workspace = Path.Combine(temp, "workspace");
        state = Path.Combine(temp, "state");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(state);
        var initial = new NodePolicy
        {
            BackupRequirement = BackupRequirement.Off,
            WorkspaceRoot = workspace,
            StateRoot = state,
            Principals =
            [
                new(
                    "test",
                    Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
                    [Scope.Observe, Scope.Ui, Scope.FilesRead, Scope.FilesWrite, Scope.MacroWrite],
                    [],
                    DateTimeOffset.UtcNow.AddHours(1))
                {
                    UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop }
                }
            ]
        };
        file = Path.Combine(state, "policy.json");
        File.WriteAllText(file, JsonSerializer.Serialize(initial));
        policy = NodePolicy.Load(file);
        dispatcher = new ActionDispatcher(ui, policy);
    }

    public static async Task<int> RunAsync(string temp)
    {
        var scenario = new CoreScenarioCases(temp);
        await scenario.RunStepsAsync();
        return scenario.count;
    }

    private async Task RunStepsAsync()
    {
        CheckCredentialsAndModules();
        CheckWorkspaceFiles();
        CheckSchemaStringLengths();
        CheckMaximumTreeDepth();
        CheckElementFilters();
        CheckObservationWaiting();
        CheckNameContains();
        CheckMatchCountWaiting();
        CheckFindElementsContract();
        CheckSearchWithinContainer();
        CheckDeepTreeSearch();
        CheckClicks();
        CheckPostconditionWaiting();
        CheckRequiredPostcondition();
        CheckRequiredPostconditionInMacro();
        CheckUnexpectedDialog();
        CheckStoppedWait();
        CheckMacros();
        CheckRestartAndDesktopSessions();
        CheckAuditAndOracles();
        await CheckClassifierAsync();
        await CheckEndpointsAndFramesAsync();
        await CheckRevocationAndAuditAsync();
    }

    // Throws Exception naming the check that failed.
    private void Check(bool value, string name)
    {
        if (!value)
            throw new Exception("FAILED: " + name);
        count++;
    }

    private JsonObject Run(string action, JsonObject? args = null)
    {
        var request = new JsonObject { ["action"] = action, ["args"] = args?.DeepClone() ?? new JsonObject() };
        return dispatcher.Execute(JsonNode.Parse(request.ToJsonString())!.AsObject(), context);
    }

    private static JsonObject Mutation(JsonObject? args = null)
    {
        args ??= new();
        args["request_id"] = Guid.CreateVersion7().ToString();
        return args;
    }

    private void CheckCredentialsAndModules()
    {
        Check(policy.Authenticate(token) == "test", "valid credential");
        JsonObject injectedControl = dispatcher.Execute(new JsonObject
        {
            ["action"] = "get_machine_capabilities",
            ["args"] = new JsonObject(),
            ["control"] = "inventory_permission",
            ["enabled"] = true
        }, context);
        Check(injectedControl["success"]?.GetValue<bool>() == false && injectedControl["outcome"]?.ToString() == "not_dispatched",
            "MCP action cannot smuggle an administrative control frame");
        try
        {
            policy.Authenticate(new string('x', 40));
            throw new Exception("credential accepted");
        }
        catch (UnauthorizedAccessException) { count++; }
        var denied = Run("enable_mcp", new()
        {
            ["id"] = "terminal"
        });
        Check(denied["success"]!.GetValue<bool>() == false, "model cannot enable modules");
        denied = Run("run_terminal_command", Mutation(new()
        {
            ["command"] = "echo unsafe"
        }));
        Check(denied["success"]!.GetValue<bool>() == false, "shell denied before launch");
    }

    private void CheckWorkspaceFiles()
    {
        var write = Run("fs_write_file", Mutation(new()
        {
            ["path"] = "literal.txt",
            ["content"] = "000123{ENTER}%^$",
            ["overwrite"] = false
        }));
        Check(write["success"]!.GetValue<bool>(), "workspace write");
        var read = Run("fs_read_file", new()
        {
            ["path"] = "literal.txt",
            ["offset"] = 1,
            ["length"] = 3
        });
        Check(read["content"]!.ToString() == "001", "bounded offset read");
        Check(!Run("fs_write_file", Mutation(new()
        {
            ["path"] = "../escape",
            ["content"] = "bad"
        }))["success"]!.GetValue<bool>(), "path escape denied");
        File.WriteAllText(Path.Combine(temp, "outside"), "private");
        File.CreateSymbolicLink(Path.Combine(workspace, "link"), Path.Combine(temp, "outside"));
        Check(!Run("fs_read_file", new()
        {
            ["path"] = "link"
        })["success"]!.GetValue<bool>(), "link escape denied");
        Check(!Run("fs_read_file", new()
        {
            ["path"] = "literal.txt",
            ["extra"] = true
        })["success"]!.GetValue<bool>(), "unknown arguments denied");
    }

    private void CheckSchemaStringLengths()
    {
        var stringLengthBounds = new Dictionary<string, JsonObject>
        {
            ["x"] = new JsonObject { ["type"] = "string", ["minLength"] = 2 },
            ["xx"] = new JsonObject { ["type"] = "string", ["maxLength"] = 1 }
        };
        foreach ((string value, JsonObject schema) in stringLengthBounds)
        {
            bool rejected = false;
            try
            {
                ToolContract.Validate(schema, JsonValue.Create(value));
            }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "declared string-length bound enforced");
        }
        ToolContract.Validate(new JsonObject { ["type"] = "string", ["maxLength"] = 1 }, JsonValue.Create("🐙"));
        Check(true, "schema string length counts Unicode code points");
    }

    private void CheckMaximumTreeDepth()
    {
        const int MaximumTreeDepth = 32;
        JsonObject arguments = new()
        {
            ["max_depth"] = MaximumTreeDepth
        };
        JsonObject schema = ToolContract.Get(Tool.GetUiTree)["inputSchema"]!.AsObject();
        ToolContract.Validate(schema, arguments);
        Check(true, "get_ui_tree accepts the maximum tree depth");

        arguments["max_depth"] = MaximumTreeDepth + 1;
        bool rejected = false;
        try
        {
            ToolContract.Validate(schema, arguments);
        }
        catch (ArgumentException) { rejected = true; }
        Check(rejected, "get_ui_tree rejects a tree depth above the maximum");
    }

    private void CheckElementFilters()
    {
        ui.Observation = new JsonObject
        {
            ["Children"] = new JsonArray(
            new JsonObject { ["Id"] = 901, ["Role"] = "Edit", ["IsOffscreen"] = false, ["IsEnabled"] = true },
            new JsonObject { ["Id"] = 902, ["Role"] = "Edit", ["IsOffscreen"] = true, ["IsEnabled"] = true },
            new JsonObject { ["Id"] = 903, ["Role"] = "Edit", ["IsOffscreen"] = false, ["IsEnabled"] = false },
            new JsonObject { ["Id"] = 904, ["Role"] = "Edit" })
        };
        var filtered = Run("find_elements", new()
        {
            ["role"] = "Edit",
            ["visible"] = true,
            ["enabled"] = true
        });
        Check(
            filtered["total_matches"]!.GetValue<int>() == 1 && filtered["matches"]![0]!["Id"]!.GetValue<int>() == 901,
            "visible enabled selection excludes hidden, disabled and unknown controls");
        filtered = Run("find_elements", new()
        {
            ["role"] = "Edit",
            ["visible"] = false
        });
        Check(filtered["total_matches"]!.GetValue<int>() == 1
            && filtered["matches"]![0]!["Id"]!.GetValue<int>() == 902, "explicit hidden filter is not inverted");
        filtered = Run("find_elements", new()
        {
            ["role"] = "Edit",
            ["enabled"] = false
        });
        Check(filtered["total_matches"]!.GetValue<int>() == 1
            && filtered["matches"]![0]!["Id"]!.GetValue<int>() == 903, "disabled filter excludes unknown provider state");
        Check(!Run("find_elements", new()
        {
            ["visible"] = "true"
        })["success"]!.GetValue<bool>(), "visibility must be boolean");
    }

    private const string Order4711Row = "bestellung@cafe-mond.lab.test, 11:54, Bestellung 4711, Unread";
    private const string Order4712Row = "bestellung@cafe-mond.lab.test, 11:55, Bestellung 4712, Unread";
    private const string InboxFolder = "Inbox, 4 unread messages";

    private static JsonObject NamedTree(params string[] names)
    {
        var children = new JsonArray();
        int identifier = 1000;
        foreach (string name in names)
        {
            children.Add(new JsonObject { ["Id"] = ++identifier, ["Role"] = "ListItem", ["Name"] = name });
        }
        return new JsonObject { ["Children"] = children };
    }

    private void CheckNameContains()
    {
        ui.Observation = NamedTree(Order4711Row, Order4712Row, InboxFolder);
        JsonObject found = Run("find_elements", new()
        {
            ["name_contains"] = "Bestellung 4712"
        });
        Check(found["total_matches"]!.GetValue<int>() == 1
            && found["matches"]![0]!["Name"]!.ToString() == Order4712Row, "name_contains selects the row by its subject");
        found = Run("find_elements", new()
        {
            ["name_contains"] = "inbox"
        });
        Check(found["total_matches"]!.GetValue<int>() == 1
            && found["matches"]![0]!["Name"]!.ToString() == InboxFolder, "name_contains ignores case");
        found = Run("find_elements", new()
        {
            ["name"] = InboxFolder,
            ["name_contains"] = "Bestellung"
        });
        Check(found["total_matches"]!.GetValue<int>() == 0, "name and name_contains must both hold");
        found = Run("find_elements", new()
        {
            ["name"] = InboxFolder,
            ["name_contains"] = "unread"
        });
        Check(found["total_matches"]!.GetValue<int>() == 1, "name and name_contains match together");
        Check(!found.ContainsKey("count_reached"), "count_reached is absent without count");
    }

    private void CheckMatchCountWaiting()
    {
        ui.Observation = null;
        int observations = 0;
        ui.ObservationFactory = ()
            => ++observations == 1 ? NamedTree(Order4711Row, Order4712Row, "Bestellung 4713", "Bestellung 4714") : NamedTree(Order4712Row);
        JsonObject found = Run("find_elements", new()
        {
            ["name_contains"] = "Bestellung",
            ["count"] = 1,
            ["timeout_ms"] = 2000
        });
        Check(found["total_matches"]!.GetValue<int>() == 1 && found["count_reached"]!.GetValue<bool>()
            && observations == 2, "count waits until the list has shrunk to the requested number");
        ui.ObservationFactory = () => NamedTree(Order4711Row, Order4712Row);
        var waiting = Stopwatch.StartNew();
        found = Run("find_elements", new()
        {
            ["name_contains"] = "Bestellung",
            ["count"] = 1,
            ["timeout_ms"] = 200
        });
        Check(!found["count_reached"]!.GetValue<bool>() && found["total_matches"]!.GetValue<int>() == 2
            && waiting.ElapsedMilliseconds >= 150, "count that is never reached returns after the timeout");
        found = Run("find_elements", new()
        {
            ["name_contains"] = "Absent",
            ["count"] = 0,
            ["timeout_ms"] = 200
        });
        Check(found["count_reached"]!.GetValue<bool>(), "count 0 is reached by an empty result");
        ui.ObservationFactory = null;
    }

    private void CheckFindElementsContract()
    {
        Check(!Run("find_elements", new()
        {
            ["name_contains"] = ""
        })["success"]!.GetValue<bool>(), "empty name_contains is rejected");
        Check(!Run("find_elements", new()
        {
            ["count"] = 201
        })["success"]!.GetValue<bool>(), "count above 200 is rejected");
    }

    private static JsonObject Element(int identifier, string role, string? name, string? value, params JsonObject[] children)
    {
        var element = new JsonObject { ["Id"] = identifier, ["Role"] = role };
        if (name != null)
        {
            element["Name"] = name;
        }
        if (value != null)
        {
            element["Value"] = value;
        }
        element["Children"] = new JsonArray(children.Select(child => (JsonNode)child).ToArray());
        return element;
    }

    private static JsonObject MailWindowTree(params string[] documentNames)
    {
        var children = new List<JsonObject> { Element(2001, "section", null, "Inbox") };
        int identifier = 2100;
        foreach (string documentName in documentNames)
        {
            children.Add(Element(++identifier, "document web", documentName, null,
                Element(++identifier, "section", null, "Hallo, wir bestellen " + documentName)));
        }
        return Element(2000, "frame", "Mail", null, children.ToArray());
    }

    private JsonObject FindSectionsWithin(string containerName)
    {
        return Run("find_elements", new()
        {
            ["role"] = "section",
            ["within"] = new JsonObject { ["role"] = "document web", ["name_contains"] = containerName }
        });
    }

    private void CheckSearchWithinContainer()
    {
        ui.Observation = MailWindowTree("Bestellung 4711", "Bestellung 4712");
        JsonObject found = Run("find_elements", new()
        {
            ["role"] = "section"
        });
        Check(found["total_matches"]!.GetValue<int>() == 3, "without within every section matches");
        found = FindSectionsWithin("4711");
        Check(found["success"]!.GetValue<bool>() && found["total_matches"]!.GetValue<int>() == 1
            && found["matches"]![0]!["Value"]!.ToString().Contains("4711"), "within limits the search to the container");
        found = FindSectionsWithin("Bestellung");
        Check(!found["success"]!.GetValue<bool>() && found["container_matches"]!.GetValue<int>() == 2
            && found["error"]!.ToString() == "Container is absent or ambiguous.", "within with two containers fails");
        found = FindSectionsWithin("9999");
        Check(!found["success"]!.GetValue<bool>() && found["container_matches"]!.GetValue<int>() == 0,
            "within with no container fails");
        Check(!Run("find_elements", new()
        {
            ["within"] = new JsonObject { ["name_contains"] = "4711" }
        })["success"]!.GetValue<bool>(), "within without role is rejected");
        Check(!Run("find_elements", new()
        {
            ["within"] = new JsonObject { ["role"] = "document web", ["name_contains"] = "" }
        })["success"]!.GetValue<bool>(), "empty within name_contains is rejected");
        ui.Observation = null;
    }

    private const int DeepTreeLevels = 32;

    private void CheckDeepTreeSearch()
    {
        JsonObject deepest = Element(3000 + DeepTreeLevels, "section", "Deepest", null);
        for (int level = DeepTreeLevels - 1; level >= 0; level--)
        {
            deepest = Element(3000 + level, "pane", "Level " + level, null, deepest);
        }
        ui.Observation = deepest;
        JsonObject found = Run("find_elements", new()
        {
            ["name"] = "Deepest"
        });
        Check(found["success"]!.GetValue<bool>() && found["total_matches"]!.GetValue<int>() == 1,
            "a tree read to depth 32 is searched without exceeding the JSON depth limit");
        ui.Observation = null;
    }

    private void CheckObservationWaiting()
    {
        ui.Observation = null;
        int observations = 0;
        ui.ObservationFactory = ()
            => ++observations == 1
                ? new JsonObject { ["Children"] = new JsonArray() }
                : new VisualTreeNode { Id = 905, Role = "Edit", IsEnabled = true };
        JsonObject filtered = Run("find_elements", new()
        {
            ["role"] = "Edit",
            ["visible"] = true,
            ["timeout_ms"] = 500
        });
        Check(filtered["total_matches"]!.GetValue<int>() == 1
            && observations == 2, "observation waiting detects a later dialog control");
        observations = 0;
        ui.ObservationFactory = () =>
        {
            observations++;
            return new JsonObject
            {
                ["Children"] = new JsonArray(
                    JsonSerializer.SerializeToNode(new VisualTreeNode { Id = 906, Role = "Edit" }),
                    JsonSerializer.SerializeToNode(new VisualTreeNode { Id = 907, Role = "Edit" }))
            };
        };
        filtered = Run("find_elements", new()
        {
            ["role"] = "Edit",
            ["timeout_ms"] = 500
        });
        Check(filtered["ambiguous"]!.GetValue<bool>()
            && observations == 1, "ambiguous observation is returned immediately without waiting it down");
        ui.ObservationFactory = null;
    }

    private void CheckClicks()
    {
        string snapshot = Run("get_ui_tree")["snapshot_id"]!.ToString();
        var click = Mutation(new()
        {
            ["id"] = ui.Id,
            ["snapshot_id"] = snapshot
        });
        var clicked = Run("click_element", click);
        Check(clicked["outcome"]!.ToString() == "dispatched" && ui.Clicks == 1, "dispatch is not goal success");
        Run("click_element", click);
        Check(ui.Clicks == 1, "same operation is not executed twice");
        Run("get_ui_tree");
        Check(!Run("click_element", Mutation(new()
        {
            ["id"] = ui.Id,
            ["snapshot_id"] = snapshot
        }))["success"]!.GetValue<bool>() && ui.Clicks == 1, "stale snapshot denied");
        snapshot = Run("get_ui_tree")["snapshot_id"]!.ToString();
        var failedCondition = Run("click_element", Mutation(new()
        {
            ["id"] = ui.Id,
            ["snapshot_id"] = snapshot,
            ["postcondition"] = new JsonObject { ["name"] = "button", ["expected_text"] = "not present" }
        }));
        Check(failedCondition["outcome"]!.ToString() == "postcondition_failed", "wrong postcondition cannot succeed");
        var other = dispatcher.Execute(
            new JsonObject { ["action"] = "end_session", ["args"] = Mutation() },
            new RequestContext("test", new string('b', 32)));
        Check(!other["success"]!.GetValue<bool>(), "other session cannot release lease");
    }

    private const int DefaultPostconditionMilliseconds = 30000;
    private const int SlowLoadMilliseconds = 15000;
    private const int LongerThanAllowedMilliseconds = 30001;

    private void CheckPostconditionWaiting()
    {
        JsonObject slowLoad = new()
        {
            ["name"] = "button",
            ["timeout_ms"] = SlowLoadMilliseconds
        };
        ClickWith(slowLoad);
        Check(ui.VerifyTimeout == SlowLoadMilliseconds, "the postcondition waits as long as the agent asked");

        ClickWith(new JsonObject { ["name"] = "button" });
        Check(ui.VerifyTimeout == DefaultPostconditionMilliseconds, "a postcondition without a wait waits thirty seconds");

        int clicksBefore = ui.Clicks;
        JsonObject tooLong = new()
        {
            ["name"] = "button",
            ["timeout_ms"] = LongerThanAllowedMilliseconds
        };
        JsonObject refused = ClickWith(tooLong);
        Check(refused["outcome"]!.ToString() == "not_dispatched" && ui.Clicks == clicksBefore, "a wait beyond the limit is refused");
    }

    private void CheckRequiredPostcondition()
    {
        policy.RequirePostcondition = true;
        int clicksBefore = ui.Clicks;
        JsonObject refused = ClickWith(null);
        Check(refused["outcome"]!.ToString() == "not_dispatched" && ui.Clicks == clicksBefore,
            "a click without its expected result is refused before dispatch when the node requires one");

        JsonObject accepted = ClickWith(new JsonObject { ["name"] = "button" });
        Check(accepted["outcome"]!.ToString() == "verified" && ui.Clicks == clicksBefore + 1,
            "a click that names its expected result runs when the node requires one");

        JsonNode? required = Run("get_machine_capabilities")["capabilities"]?["postconditionRequired"];
        Check(required?.GetValue<bool>() == true, "the capabilities tell the agent that a postcondition is required");
        policy.RequirePostcondition = false;

        JsonObject unrestricted = ClickWith(null);
        Check(unrestricted["outcome"]!.ToString() == "dispatched", "without the setting a click needs no postcondition");
    }

    // The fake desktop clicks whatever id it gets; the run supplies the snapshot of its own observation.
    private const int AnyObservedElement = 1;

    private void CheckRequiredPostconditionInMacro()
    {
        var macro = new MacroDefinition
        {
            Name = "click_once",
            Steps =
            [
                new() { ["action"] = "get_ui_tree", ["args"] = new JsonObject() },
                new() { ["action"] = "click_element", ["args"] = new JsonObject { ["id"] = AnyObservedElement } }
            ]
        };
        var engine = new MacroEngine(Path.Combine(state, "required-postcondition.json"));
        engine.SaveMacro(macro);
        int clicksBefore = ui.Clicks;

        policy.RequirePostcondition = true;
        JsonNode refused = JsonSerializer.SerializeToNode(engine.RunMacro("click_once", null, Dispatch, macro.Sha256))!;
        Check(refused["success"]!.GetValue<bool>() == false && ui.Clicks == clicksBefore,
            "a macro step without its expected result is refused when the node requires one");
        policy.RequirePostcondition = false;

        JsonNode allowed = JsonSerializer.SerializeToNode(engine.RunMacro("click_once", null, Dispatch, macro.Sha256))!;
        Check(allowed["success"]!.GetValue<bool>() && ui.Clicks == clicksBefore + 1, "without the setting the macro step runs");
    }

    private const int LongWaitMilliseconds = 10000;
    private const int ShortWaitMilliseconds = 600;
    private const int PromptAnswerMilliseconds = 3000;

    private static readonly UiTargetIdentity MailButton =
        new(Environment.ProcessId, Environment.ProcessPath!, "Posteingang", "Button", "Abrufen", "", true, true);

    private void CheckUnexpectedDialog()
    {
        ui.Identity = MailButton;
        ui.AfterClick = () => ui.ModalWindowTitle = "Sicherheitswarnung";
        JsonObject neverLoads = new()
        {
            ["name"] = "button",
            ["expected_text"] = "not present",
            ["timeout_ms"] = LongWaitMilliseconds
        };
        var waited = System.Diagnostics.Stopwatch.StartNew();

        JsonObject interrupted = ClickWith(neverLoads);

        Check(interrupted["outcome"]!.ToString() == "postcondition_failed" && waited.ElapsedMilliseconds < PromptAnswerMilliseconds
            && interrupted["unexpected_dialog"]?["window_title"]?.ToString() == "Sicherheitswarnung",
            "a dialog that opens instead of the expected result ends the wait at once and is named");

        ui.AfterClick = null;
        JsonObject alreadyOpen = new()
        {
            ["name"] = "button",
            ["expected_text"] = "not present",
            ["timeout_ms"] = ShortWaitMilliseconds
        };
        waited.Restart();
        JsonObject waitedOut = ClickWith(alreadyOpen);
        Check(waitedOut["unexpected_dialog"] == null && waited.ElapsedMilliseconds >= ShortWaitMilliseconds,
            "a dialog that was already open before the action does not end the wait");
        ui.ModalWindowTitle = null;
        ui.Identity = null;
    }

    private void CheckStoppedWait()
    {
        var wait = new PostconditionWait(ui, () => false);
        JsonObject neverLoads = new()
        {
            ["name"] = "button",
            ["expected_text"] = "not present",
            ["timeout_ms"] = LongWaitMilliseconds
        };
        var waited = System.Diagnostics.Stopwatch.StartNew();

        JsonObject stopped = wait.Wait(neverLoads, new DialogWatch(Environment.ProcessId, null));

        Check(stopped["stopped_by_person"]?.GetValue<bool>() == true && waited.ElapsedMilliseconds < PromptAnswerMilliseconds,
            "a person who stops the session ends the wait at once");
    }

    private JsonObject Dispatch(JsonObject request)
    {
        return dispatcher.Execute(request, context);
    }

    private JsonObject ClickWith(JsonObject? postcondition)
    {
        string snapshot = Run("get_ui_tree")["snapshot_id"]!.ToString();
        JsonObject arguments = Mutation(new()
        {
            ["id"] = ui.Id,
            ["snapshot_id"] = snapshot
        });
        if (postcondition != null)
        {
            arguments["postcondition"] = postcondition;
        }
        return Run("click_element", arguments);
    }

    private void CheckMacros()
    {
        var macro = new MacroDefinition
        {
            Name = "literal",
            Parameters = [new() { Name = "value", Type = "string" }],
            Steps =
            [
                new() { ["action"] = "get_ui_tree", ["args"] = new JsonObject(), ["capture"] = "view" },
                new() { ["action"] = "type_text", ["args"] = new JsonObject { ["id"] = "{{id}}", ["text"] = "{{value}}" } }
            ]
        };
        macro.Parameters.Add(new()
        {
            Name = "id",
            Type = "integer"
        });
        var engine = new MacroEngine(Path.Combine(state, "versions.json"));
        engine.SaveMacro(macro);
        var generated = new MacroDefinition
        {
            Name = "typed",
            Parameters = [new() { Name = "value", Type = "string" }],
            Steps =
            [
                new()
                {
                    ["action"] = "fs_write_file",
                    ["args"] = new JsonObject { ["path"] = "macro.txt", ["content"] = "{{value}}", ["overwrite"] = true }
                }
            ]
        };
        engine.SaveMacro(generated);
        var recipeArguments = JsonNode.Parse("{\"value\":\"000123\"}")!.AsObject();
        var recipeRun = engine.RunMacro("typed", recipeArguments, request => dispatcher.Execute(request, context), generated.Sha256);
        var recipeResult = JsonSerializer.SerializeToNode(recipeRun)!;
        Check(recipeResult["success"]!.GetValue<bool>()
            && File.ReadAllText(Path.Combine(workspace, "macro.txt")) == "000123", "macro preserves numeric strings");
    }

    private void CheckRestartAndDesktopSessions()
    {
        int clicksBefore = ui.Clicks;
        var second = new ActionDispatcher(ui, policy);
        second.Execute(
            JsonNode.Parse(new JsonObject { ["action"] = "click_element", ["args"] = click.DeepClone() }.ToJsonString())!.AsObject(),
            context);
        Check(ui.Clicks == clicksBefore, "completed operation survives worker restart without replay");
        JsonObject inputObservation = Run("get_ui_tree");
        int inputsBefore = ui.PhysicalTexts;
        ui.DesktopSession = new DesktopSessionInfo(2, 1, "Disconnected", "Default", "Default", false);
        JsonObject inactiveInput = Run("type_text", Mutation(new JsonObject
        {
            ["id"] = ui.Id,
            ["snapshot_id"] = inputObservation["snapshot_id"]!.ToString(),
            ["text"] = "must not reach the desktop"
        }));
        Check(inactiveInput["outcome"]?.ToString() == "not_dispatched" && ui.PhysicalTexts == inputsBefore,
            "Disconnected desktop is rejected before input even when its UIA controls remain available");
        Check(Run("get_ui_tree")["outcome"]?.ToString() == "not_dispatched",
            "Disconnected desktop cannot expose a cached UI tree without a visible activity indicator");
        JsonObject diagnostic = Run("get_machine_capabilities");
        Check(diagnostic["capabilities"]?["desktopSession"]?["SessionId"]?.GetValue<int>() == 2,
            "Capabilities identify the actual operated Windows session");
        ui.DesktopSession = new DesktopSessionInfo(2, 1, "Active", "Default", "Default", true);
        JsonObject activeInput = Run("type_text", Mutation(new JsonObject
        {
            ["id"] = ui.Id,
            ["snapshot_id"] = inputObservation["snapshot_id"]!.ToString(),
            ["text"] = "connected RDP input"
        }));
        Check(activeInput["success"]?.GetValue<bool>() == true && ui.PhysicalTexts == inputsBefore + 1,
            "An active RDP desktop remains usable when it differs from the console session");
        ui.DesktopSession = null;
    }

    private void CheckAuditAndOracles()
    {
        Check(!File.ReadAllText(Path.Combine(state, "audit.jsonl")).Contains("000123{ENTER}"), "audit excludes sensitive arguments");
        var badVersion = Mutation(new()
        {
            ["path"] = "uuid4.txt",
            ["content"] = "bad"
        });
        badVersion["request_id"] = Guid.NewGuid().ToString();
        Check(!Run("fs_write_file", badVersion)["success"]!.GetValue<bool>()
            && !File.Exists(Path.Combine(workspace, "uuid4.txt")), "UUIDv4 mutation IDs refused");
        var artifacts = EvaluationRunner.VerifyArtifacts(
            workspace,
            new JsonArray(new JsonObject { ["path"] = "literal.txt", ["text_equals"] = "wrong" }));
        Check(!artifacts[0]!["passed"]!.GetValue<bool>(), "independent oracle rejects wrong artifacts");
        var exactOracle = new JsonObject { ["path"] = "literal.txt", ["text_equals"] = "000123{ENTER}%^$" };
        artifacts = EvaluationRunner.VerifyArtifacts(workspace, new JsonArray(exactOracle));
        Check(artifacts[0]!["passed"]!.GetValue<bool>(), "independent oracle verifies exact artifacts");
        using (var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            string privateKey = Path.Combine(state, "signing.pem"), publicKey = Path.Combine(state, "public.pem");
            File.WriteAllText(privateKey, signingKey.ExportPkcs8PrivateKeyPem());
            File.WriteAllText(publicKey, signingKey.ExportSubjectPublicKeyInfoPem());
            var report = new JsonObject { ["hard_pass"] = true, ["case_sha256"] = "fixture" };
            string signature = EvaluationRunner.SignReport(report, privateKey);
            Check(EvaluationRunner.VerifyReport(report, signature, publicKey), "signed evaluator report");
            report["hard_pass"] = false;
            Check(!EvaluationRunner.VerifyReport(report, signature, publicKey), "tampered evaluator report rejected");
        }
    }

    private async Task CheckClassifierAsync()
    {
        string layaCredential = Path.Combine(state, "laya-credential");
        File.WriteAllText(layaCredential, new string('x', 40));
        var layaConfig = new LayaAdvisoryConfig
        {
            Enabled = true,
            Endpoint = "http://127.0.0.1:8099",
            AllowLoopbackHttp = true,
            CredentialFile = layaCredential,
            ModelRevision = new string('a', 40)
        };
        var questions = JsonNode.Parse(
            "{\"quality\":{\"type\":\"choice\",\"instructions\":\"Does evidence support the goal?\","
            + "\"criteria\":{\"pass\":\"supported\",\"fail\":\"unsupported\"}}}")!.AsObject();
        var handler = new ClassifierFixture();
        using (var laya = new LayaAdvisoryEvaluator(layaConfig, handler))
        {
            var result = (await laya.EvaluateAsync(new JsonObject { ["trace"] = "fixture" }, questions)).ToJson();
            Check(result["outcome"]!.ToString() == "advisory" && result["abstain_from_automatic_verdict"]!.GetValue<bool>()
                && !result["can_override_assertions"]!.GetValue<bool>(), "uncalibrated classifier cannot certify a task");
            Check(
                result["provider"]!.ToString() == "laya" && !result["can_grant_permissions"]!.GetValue<bool>(),
                "the answer names its provider and claims no authority");
            Check(handler.RequestVersion == 7, "classifier requests use UUIDv7");
            handler.Truncated = true;
            result = (await laya.EvaluateAsync(new JsonObject(), questions)).ToJson();
            Check(result["outcome"]!.ToString() == "uncertain", "truncated classifier evidence abstains");
            handler.Truncated = false;
            handler.InventedLabel = true;
            result = (await laya.EvaluateAsync(new JsonObject(), questions)).ToJson();
            Check(!result["success"]!.GetValue<bool>()
                && result["outcome"]!.ToString() == "uncertain", "invented classifier label refused");
        }
    }

    private async Task CheckEndpointsAndFramesAsync()
    {
        foreach (string endpoint in new[] { "http://192.0.2.2:8000", "http://localhost:8000", "https://example.com" })
        {
            try
            {
                using var rejected = new LayaAdvisoryEvaluator(
                    new LayaAdvisoryConfig
                    {
                        Enabled = true,
                        Endpoint = endpoint,
                        AllowLoopbackHttp = true,
                        ModelRevision = new string('a', 40)
                    });
                throw new Exception("unsafe classifier endpoint accepted");
            }
            catch (ArgumentException) { count++; }
        }
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"ok\":true}\n")))
            Check(await new JsonLineReader(stream).ReadAsync() == "{\"ok\":true}", "complete JSON frame parsed");
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("123456789\n")))
        {
            try
            {
                await new JsonLineReader(stream, 4).ReadAsync();
                throw new Exception("oversized frame accepted");
            }
            catch (InvalidDataException) { count++; }
        }
    }

    private async Task CheckRevocationAndAuditAsync()
    {
        policy.Principals = [policy.Principals[0] with { Scopes = [Scope.Observe] }];
        File.WriteAllText(file, JsonSerializer.Serialize(policy));
        Check(!Run("fs_read_file", new()
        {
            ["path"] = "literal.txt"
        })["success"]!.GetValue<bool>(), "capability revocation on next call");
        policy.Principals = [policy.Principals[0] with { TokenSha256 = new string('0', 64) }];
        File.WriteAllText(file, JsonSerializer.Serialize(policy));
        try
        {
            policy.Authenticate(token);
            throw new Exception("revoked credential accepted");
        }
        catch (UnauthorizedAccessException) { count++; }
        using (var actor = new DesktopActor(dispatcher))
        {
            var requests = Enumerable.Range(0, 16).Select(_ => actor.ExecuteAsync(
                new JsonObject { ["action"] = "get_machine_capabilities", ["args"] = new JsonObject() },
                context));
            await Task.WhenAll(requests);
            Check(true, "bounded actor handles concurrent requests");
        }
        string log = Path.Combine(state, "audit.jsonl");
        File.AppendAllText(log, "{\"version\":1}\n");
        try
        {
            _ = new AuditJournal(state);
            throw new Exception("tampered audit accepted");
        }
        catch (Exception ex) when (ex is InvalidDataException or NullReferenceException) { count++; }
    }
}
