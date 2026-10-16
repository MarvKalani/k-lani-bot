using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;

// The node compares Tool values; clients send the words of contracts/tools.json. Both lists must name the same tools,
// or a tool would be offered that no code knows, or known to code but never offered.
internal static class ToolCases
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Tool: " + name);
            }
            passed++;
        }

        string[] contractNames = ToolContract.Tools.OfType<JsonObject>().Select(tool => tool["name"]!.ToString()).ToArray();
        string[] enumNames = Enum.GetValues<Tool>().Select(ToolNames.Of).ToArray();
        Check(contractNames.Order().SequenceEqual(enumNames.Order()), "enum and contract name the same tools");
        Check(enumNames.Distinct().Count() == enumNames.Length, "no two enum values share a word");
        foreach (Tool tool in Enum.GetValues<Tool>())
        {
            Check(ToolNames.Parse(ToolNames.Of(tool)) == tool, "round trip of " + tool);
        }
        Check(!ToolNames.TryParse("Click_Element", out _), "words are matched exactly");
        Check(JsonSerializer.Serialize(Tool.TypeText) == "\"type_text\"", "a tool is written as its word");
        Check(RefusedAsJson("0") && RefusedAsJson("\"teleport\""), "only known words are read as a tool");
        return passed;
    }

    private static bool RefusedAsJson(string json)
    {
        try
        {
            JsonSerializer.Deserialize<Tool>(json);
            return false;
        }
        catch (JsonException)
        {
            return true;
        }
    }
}
