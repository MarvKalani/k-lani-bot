using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Common;

// Features are words in the embedded tool contract and in get_machine_capabilities. Every word in the contract must be a
// feature, and the answer must keep the words a client already knows.
internal static class ToolFeatureCases
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Tool feature: " + name);
            }
            passed++;
        }

        foreach (JsonObject tool in ToolContract.Tools.OfType<JsonObject>())
        {
            string? word = tool["feature"]?.ToString();
            Check(WireNames<ToolFeature>.TryParse(word, out _), "tool " + tool["name"] + " names a known feature");
        }
        foreach (ToolFeature feature in Enum.GetValues<ToolFeature>())
        {
            string word = WireNames<ToolFeature>.Of(feature);
            Check(WireNames<ToolFeature>.TryParse(word, out ToolFeature parsed) && parsed == feature, "round trip of " + feature);
        }
        string written = JsonSerializer.Serialize(new HashSet<ToolFeature> { ToolFeature.SemanticInspect, ToolFeature.WindowControl });
        Check(written == "[\"semanticInspect\",\"windowControl\"]", "a feature is written as its word");
        Check(!ToolFeatures.UsesDesktop(ToolFeature.Filesystem), "files are handled inside the node");
        Check(ToolFeatures.UsesDesktop(ToolFeature.Fleet), "no backend offers the gateway's fleet tool");
        Check(!ToolFeatures.ShowsBorder(ToolFeature.Fleet), "the gateway's fleet tool shows no border");
        Check(ToolFeatures.ShowsBorder(ToolFeature.Clipboard), "the clipboard shows the border");
        return passed;
    }
}
