using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using KLaniBot.Common;

namespace KLaniBot.Guard;

// Use case: policy.json grants a principal scopes, and every tool in contracts/tools.json names the one scope it needs.
// The words are the spelling in both files; inside the node only these values are compared. There is no "all": every
// scope is granted by name.
[JsonConverter(typeof(ScopeJsonConverter))]
public enum Scope
{
    // Read the screen and the controls. Every session needs it.
    [JsonStringEnumMemberName("observe")]
    Observe,

    // Click, type and operate programs.
    [JsonStringEnumMemberName("ui")]
    Ui,

    [JsonStringEnumMemberName("inventory.read")]
    InventoryRead,

    [JsonStringEnumMemberName("clipboard")]
    Clipboard,

    // Files inside the agent workspace only.
    [JsonStringEnumMemberName("files.read")]
    FilesRead,

    [JsonStringEnumMemberName("files.write")]
    FilesWrite,

    [JsonStringEnumMemberName("macro.write")]
    MacroWrite,

    // Start the programs the principal's Applications list allows.
    [JsonStringEnumMemberName("process")]
    Process,

    [JsonStringEnumMemberName("shell")]
    Shell,

    // Run the trusted evaluator; not granted by --init.
    [JsonStringEnumMemberName("eval")]
    Eval,

    // Plan a backup of the operator's backup source and verify existing archives; not granted by --init.
    [JsonStringEnumMemberName("backup.read")]
    BackupRead,

    // Write an encrypted archive of the operator's backup source; not granted by --init.
    [JsonStringEnumMemberName("backup.create")]
    BackupCreate,

    // Decrypt an archive into a new folder under the operator's recovery root; not granted by --init.
    [JsonStringEnumMemberName("backup.restore")]
    BackupRestore
}

// A number in place of a word would grant whatever scope has that position today, so only words are read.
public sealed class ScopeJsonConverter() : JsonStringEnumConverter<Scope>(namingPolicy: null, allowIntegerValues: false);

// The dispatcher and the supervisor ask here which scope a contract entry requires.
public static class ToolScope
{
    // Throws InvalidDataException when the tool names no known scope. The contract is embedded and a test reads every
    // tool's scope, so this only happens with a broken build.
    public static Scope Of(JsonObject tool)
    {
        string? word = tool["scope"]?.ToString();

        if (!WireNames<Scope>.TryParse(word, out Scope scope))
        {
            throw new InvalidDataException("Tool " + tool["name"] + " names an unknown scope.");
        }
        return scope;
    }
}
