using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// First-use replies expose newly available names separately from noncallable material and storage failures.
public static class CompactAutomaticWire
{
    public static JsonObject Reply(IReadOnlyList<CompactAutomaticResult> results, bool compact)
    {
        var functions = new JsonArray(results.Select(result => (JsonNode?)Item(result, compact)).ToArray());
        var reply = new JsonObject { ["functions"] = functions };
        if (!compact || results.Any(result => result.Verified))
        {
            reply["instruction"] = "Load the selected function schema through cartridge_browse; reuse it through cartridge_call.";
        }

        return reply;
    }

    private static JsonObject Item(CompactAutomaticResult result, bool compact)
    {
        var item = new JsonObject();
        if (!compact || result.Status == CompactAutomaticStatus.Available)
        {
            item["function"] = result.Name;
        }
        item["status"] = result.Status switch
        {
            CompactAutomaticStatus.Available => "available",
            CompactAutomaticStatus.NeedsVerification => "needs_verification",
            CompactAutomaticStatus.StorageFailed => "storage_failed",
            _ => throw new UnreachableException()
        };
        AddDetails(item, result);

        return item;
    }

    private static void AddDetails(JsonObject item, CompactAutomaticResult result)
    {
        if (result.Verified)
        {
            item["verified"] = true;
        }
        if (result.Reused)
        {
            item["reused_definition"] = true;
        }
        if (result.Reason != null)
        {
            item["reason"] = result.Reason;
        }
        if (result.Material != null)
        {
            item["material"] = new JsonObject
            {
                ["action"] = CompactCaptureNames.Action(result.Material.Action),
                ["selector"] = CompactDiscoveryWire.Selector(result.Material.Selector)
            };
        }
    }
}
