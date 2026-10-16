using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Transport;

// The inspector page must follow the tool contract. The earlier copy typed into the page did not.
internal static class InspectorPageCases
{
    public static int Run()
    {
        var checks = new Checks("Inspector page");

        CheckToolTable(checks);
        string page = InspectorPage.Build();
        checks.That(
            !page.Contains("__TOOL_TABLE__", StringComparison.Ordinal) && !page.Contains("__SESSION_HEADER__", StringComparison.Ordinal),
            "no placeholder is left in the page");
        checks.That(
            page.Contains(ProtocolIdentity.SessionHeader, StringComparison.Ordinal),
            "the session header name comes from the product identity");
        checks.That(page.Contains("\"launch_process\"", StringComparison.Ordinal), "the tool table is part of the page");
        return checks.Passed;
    }

    private static void CheckToolTable(Checks checks)
    {
        var table = InspectorPage.DescribeTools();
        checks.That(table.Count == ToolContract.Tools.Count, "every tool of the contract is described");
        foreach (var tool in ToolContract.Tools.OfType<JsonObject>())
        {
            string name = tool["name"]!.ToString();
            bool mutates = tool["mutates"]!.GetValue<bool>();
            bool acceptsRequestId = tool["inputSchema"]!["properties"]?["request_id"] != null;
            bool added = table[name]!["add_request_id"]!.GetValue<bool>();
            checks.That(added == (mutates && acceptsRequestId),
                "request_id is generated exactly for mutating tools that accept one: " + name);
            if (!mutates && !acceptsRequestId)
            {
                checks.That(!added, "a reading tool without request_id never receives one: " + name);
            }
        }
        // The tools the earlier hand-written list got wrong.
        foreach (string reader in new[] { "take_screenshot", "collect_inventory", "get_macro", "get_macro_trace" })
        {
            checks.That(!table[reader]!["add_request_id"]!.GetValue<bool>(), "the schema of "
                + reader
                + " rejects a request_id, so none is added");
        }
        checks.That(!table["get_action_status"]!["add_request_id"]!.GetValue<bool>(),
            "get_action_status keeps the request_id the user typed");
        checks.That(table["click_element"]!["add_request_id"]!.GetValue<bool>(), "a mutating tool gets a generated request_id");
    }
}
