using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Advisory;
using KLaniBot.Fleet;
using KLaniBot.Startup;

namespace KLaniBot.Updates;

// Commands of the release and evaluation pipeline. They run on a build machine, never on an operated node.
internal static class ReleaseCommands
{
    private static readonly string[] ExportedToolFields = ["name", "description", "inputSchema", "annotations"];

    // scripts/release.sh lists every file of a packaged release, optionally signed with the release key.
    public static int CreateInventory(NodeArguments arguments)
    {
        JsonObject manifest = ReleaseInventory.Create(arguments.RequireOption("--inventory"));
        if (arguments.Option("--signing-key") is { } releaseKey)
        {
            var signed = new JsonObject
            {
                ["manifest"] = manifest,
                ["signature"] = EvaluationRunner.SignReport(manifest, releaseKey)
            };
            Console.WriteLine(signed.ToJsonString());
        }
        else
        {
            Console.WriteLine(manifest.ToJsonString());
        }
        return 0;
    }

    // A release candidate is run against an evaluation specification on test nodes. Exit code 2 means
    // a hard requirement failed, so a pipeline can tell that apart from a crash (1).
    public static async Task<int> EvaluateAsync(NodeArguments arguments)
    {
        using IAdvisoryEvaluator? evaluator = FleetCommands.OpenAdvisor(arguments);
        JsonObject report = await EvaluationRunner.RunAsync(
            arguments.RequireOption("--evaluate"),
            arguments.RequireOption("--nodes"),
            evaluator);
        if (arguments.Option("--signing-key") is { } key)
        {
            var signed = new JsonObject
            {
                ["report"] = report,
                ["signature"] = EvaluationRunner.SignReport(report, key)
            };
            Console.WriteLine(signed.ToJsonString());
        }
        else
        {
            Console.WriteLine(report.ToJsonString());
        }
        bool hardPass = report["hard_pass"]!.GetValue<bool>();
        return hardPass ? 0 : 2;
    }

    // scripts/verify-schema-export.sh compares this output with mcp/schemas byte for byte, so the field
    // order, the indentation and the final newline are part of the contract.
    public static int ExportSchemas(NodeArguments arguments)
    {
        string target = Path.GetFullPath(arguments.RequireOption("--export-schemas"));
        Directory.CreateDirectory(target);
        var indented = new JsonSerializerOptions { WriteIndented = true };
        foreach (JsonObject tool in ToolContract.Tools.OfType<JsonObject>())
        {
            var definition = new JsonObject();
            foreach (string field in ExportedToolFields)
            {
                definition[field] = tool[field]!.DeepClone();
            }
            string file = Path.Combine(target, tool["name"] + ".json");
            File.WriteAllText(file, definition.ToJsonString(indented) + "\n");
        }
        return 0;
    }
}
