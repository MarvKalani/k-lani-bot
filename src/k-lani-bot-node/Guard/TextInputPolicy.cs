using System.Text.Json.Serialization;

namespace KLaniBot.Guard;

// What a guarded rule lets the agent type into its element. The words are the spelling in policy.json.
// The default is None, so a rule written only to allow a click never lets text through by accident.
[JsonConverter(typeof(JsonStringEnumConverter<TextInputPolicy>))]
public enum TextInputPolicy
{
    // No text at all; the element may only be clicked.
    [JsonStringEnumMemberName("none")]
    None,

    // Any text up to the rule's length limit, which replaces the field value.
    [JsonStringEnumMemberName("literal")]
    Literal,

    // Only the full path of an existing file inside the agent workspace with one of the rule's extensions.
    // Meant for file pickers: their name field accepts any path on the host, so the path itself is checked.
    [JsonStringEnumMemberName("workspace_path")]
    WorkspacePath
}
