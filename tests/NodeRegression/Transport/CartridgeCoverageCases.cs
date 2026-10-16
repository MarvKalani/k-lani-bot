using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Transport;

// Coverage must keep absence, partial implementation, blocked access and acceptance distinct. Metadata discovery
// cannot turn an entire documented API into implemented functions or justify a completeness percentage.
internal static class CartridgeCoverageCases
{
    public static int Run()
    {
        var checks = new Checks("Cartridge coverage");
        string root = Path.Combine(RepositoryLocator.Root(), "cartridges/programs/outlook-classic/16.0.14334.20918");
        using var bundle = new GatewayCartridges(Path.Combine(root, "cartridge.json"));
        var available = Enum.GetValues<Tool>().ToHashSet();
        JsonObject summary = bundle.Coverage!.Summary(bundle.Recipes, available);
        checks.That(!summary["reference_complete"]!.GetValue<bool>() && !summary["fully_accepted"]!.GetValue<bool>()
            && summary["required_reference_items"]!.GetValue<int>() > 500,
            "a large documentation baseline is explicitly incomplete and not mistaken for full acceptance");
        CheckReadScope(checks, bundle, available);
        CheckBlockedAccess(checks, bundle, available);
        CheckMissingFunction(checks, bundle, available);
        CheckPages(checks, bundle, available);

        return checks.Passed;
    }

    private static void CheckReadScope(Checks checks, GatewayCartridges bundle, HashSet<Tool> available)
    {
        JsonObject page = bundle.Coverage!.Browse("Info/Coverage/Use/Mail/Read", bundle.Recipes, available);
        string text = page["requirements"]!.ToJsonString();
        checks.That(!text.Contains("appointmentitem", StringComparison.Ordinal) && !text.Contains("contactitem", StringComparison.Ordinal),
            "mail-reading coverage does not load unrelated calendar or contact requirements");
        JsonObject reading = page["requirements"]!.AsArray().OfType<JsonObject>()
            .Single(item => item["id"]!.ToString() == "workflow.mail-read");
        checks.That(reading["implementation"]!.ToString() == "Partial" && reading["acceptance"]!.ToString() != "RealAppTested",
            "bounded body reading and unaccepted real-app behavior remain explicit");
    }

    private static void CheckBlockedAccess(Checks checks, GatewayCartridges bundle, HashSet<Tool> available)
    {
        available.Remove(Tool.InspectApplication);
        JsonObject page = bundle.Coverage!.Browse("Info/Coverage/Use/Mail/Read", bundle.Recipes, available);
        JsonObject reading = page["requirements"]!.AsArray().OfType<JsonObject>()
            .Single(item => item["id"]!.ToString() == "workflow.mail-read");
        checks.That(reading["implementation"]!.ToString() == "Partial" && !reading["available"]!.GetValue<bool>(),
            "a blocked native route stays implemented in the bundle and is not falsely reported as absent");
    }

    private static void CheckMissingFunction(Checks checks, GatewayCartridges bundle, HashSet<Tool> available)
    {
        JsonObject page = bundle.Coverage!.Browse("Info/Coverage/Use/Mail/Send", bundle.Recipes, available);
        checks.That(page["requirements"]!.AsArray().OfType<JsonObject>()
            .Any(item => item["implementation"]!.ToString() == "Missing"),
            "message sending remains a recorded gap despite the native method catalog");
    }

    private static void CheckPages(Checks checks, GatewayCartridges bundle, HashSet<Tool> available)
    {
        JsonObject first = bundle.Coverage!.Browse("Info/Coverage", bundle.Recipes, available);
        JsonObject next = bundle.Coverage.Browse(first["next_path"]!.ToString(), bundle.Recipes, available);
        string[] initialIds = first["requirements"]!.AsArray().OfType<JsonObject>().Select(item => item["id"]!.ToString()).ToArray();
        checks.That(first["requirements"]!.AsArray().Count == 20 && next["requirements"]!.AsArray().Count == 20
            && next["requirements"]!.AsArray().OfType<JsonObject>().All(item => !initialIds.Contains(item["id"]!.ToString())),
            "large reference inventories use disjoint bounded pages without an MCP schema reload");
    }
}
