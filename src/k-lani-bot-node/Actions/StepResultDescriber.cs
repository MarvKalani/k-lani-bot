using System.Text.Json.Nodes;
using KLaniBot.Localization;

namespace KLaniBot.Actions;

// The operator sees a bounded identity result beside the completed step; mail bodies and entered values stay private.
internal static class StepResultDescriber
{
    public static string Describe(Tool tool, JsonObject result)
    {
        if (result["success"]?.GetValue<bool>() != true)
        {
            return "";
        }
        if (tool == Tool.InspectApplication && result["info"] is JsonObject info)
        {
            return UiTexts.Format(UiText.ResultApplicationIdentity, Field(info, "Variant"), Field(info, "Version"),
                Field(info, "Build"), Field(info, "Language"));
        }
        if (tool == Tool.InspectApplication && result["status"] is JsonObject status)
        {
            return UiTexts.Get(status["Running"]?.GetValue<bool>() == true ? UiText.ResultProgramRunning : UiText.ResultProgramStopped);
        }
        if (tool == Tool.ListWindows && result["windows"] is JsonArray windows)
        {
            return DescribeWindows(windows);
        }

        return "";
    }

    private static string DescribeWindows(JsonArray windows)
    {
        JsonObject? outlook = windows.OfType<JsonObject>().FirstOrDefault(window =>
            string.Equals(Field(window, "ProcessName"), "OUTLOOK", StringComparison.OrdinalIgnoreCase));

        return outlook == null ? UiTexts.Format(UiText.ResultWindowsFound, windows.Count)
            : UiTexts.Format(UiText.ResultExecutableVersion, "Outlook", Field(outlook, "AppVersion"));
    }

    private static string Field(JsonObject value, string name)
    {
        return value[name]?.ToString() ?? "";
    }
}
