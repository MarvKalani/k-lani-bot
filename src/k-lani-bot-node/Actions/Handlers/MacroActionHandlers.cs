using System.Text.Json.Nodes;
using KLaniBot.Guard;
using KLaniBot.Visibility;

namespace KLaniBot.Actions.Handlers;

// Use case: a recipe that worked once is stored and run again by name and content hash. Saving needs its
// own scope, because a recipe is a program the next run executes with the permissions of whoever runs it.
// Running checks the scope of every step before the first one starts, and each step then passes the same
// checks as a request of its own.
internal sealed class MacroActionHandlers
{
    private readonly HandlerServices _services;

    public MacroActionHandlers(HandlerServices services)
    {
        _services = services;
    }

    public void Register(Action<Tool, ActionHandler> add)
    {
        add(Tool.ListMacros, ListMacros);
        add(Tool.GetMacro, GetMacro);
        add(Tool.GetMacroTrace, GetMacroTrace);
        add(Tool.SaveMacro, SaveMacro);
        add(Tool.RunMacro, RunMacro);
    }

    private object ListMacros(ActionCall call)
    {
        int offset = call.Arguments["offset"]?.GetValue<int>() ?? 0;
        int limit = call.Arguments["limit"]?.GetValue<int>() ?? 25;
        return _services.Macros.Describe(offset, limit);
    }

    private object GetMacro(ActionCall call)
    {
        return new
        {
            success = true,
            macro = _services.Macros.Definition(call.Arguments["name"]!.ToString(), call.Arguments["sha256"]!.ToString())
        };
    }

    private object GetMacroTrace(ActionCall call)
    {
        string runId = call.Arguments["run_id"]!.ToString();
        int offset = call.Arguments["offset"]?.GetValue<int>() ?? 0;
        int limit = call.Arguments["limit"]?.GetValue<int>() ?? 5;
        return _services.Macros.ReadTrace(runId, call.Caller.PrincipalId, offset, limit);
    }

    private object SaveMacro(ActionCall call)
    {
        MacroDefinition definition = ReadDefinition(call.Arguments);
        MacroSaving saving = _services.Macros.SaveMacro(definition);
        string message = saving == MacroSaving.Saved ? "Immutable macro version saved." : "Version already exists.";
        return new
        {
            success = true,
            message,
            macro = definition
        };
    }

    private object RunMacro(ActionCall call)
    {
        string name = call.Arguments["name"]?.ToString() ?? call.Arguments["macro"]?.ToString() ?? "";
        string sha256 = call.Arguments["sha256"]!.ToString();
        foreach (Scope scope in _services.Macros.RequiredScopes(name, sha256))
        {
            _services.Policy.Authorize(call.Caller, scope);
        }
        bool trace = call.Arguments["trace"]?.GetValue<bool>() ?? false;
        return _services.Macros.RunMacro(
            name,
            call.Arguments["params"] as JsonObject,
            step => _services.RunNestedAction(step, call.Caller),
            sha256,
            call.Caller.PrincipalId,
            trace,
            PacingFor(_services.Visibility));
    }

    private static MacroPacing? PacingFor(AccessVisibility? visibility)
    {
        if (visibility == null)
        {
            return null;
        }
        return new MacroPacing(
            () => visibility.StepDelay,
            remaining => visibility.SetPlannedSteps(remaining.Select(StepDescriber.DescribeMacroStep).ToArray()));
    }

    private static MacroDefinition ReadDefinition(JsonObject arguments)
    {
        var definition = new MacroDefinition
        {
            Name = arguments["name"]?.ToString() ?? "",
            Description = arguments["description"]?.ToString() ?? ""
        };
        if (arguments["parameters"] is JsonArray parameters)
        {
            foreach (JsonObject parameter in parameters.OfType<JsonObject>())
            {
                definition.Parameters.Add(new MacroParameter
                {
                    Name = parameter["name"]?.ToString() ?? "",
                    Description = parameter["description"]?.ToString() ?? "",
                    Default = parameter["default"]?.DeepClone(),
                    Type = parameter["type"]?.ToString() ?? "string",
                    Required = parameter["required"]?.GetValue<bool?>() ?? true
                });
            }
        }
        if (arguments["steps"] is JsonArray steps)
        {
            foreach (JsonObject step in steps.OfType<JsonObject>())
            {
                definition.Steps.Add(step);
            }
        }
        if (arguments["postconditions"] is JsonArray conditions)
        {
            definition.Postconditions = conditions.OfType<JsonObject>().Select(condition => (JsonObject)condition.DeepClone()).ToList();
        }
        return definition;
    }
}
