using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Platforms;

namespace KLaniBot.Transport.CompactUi;

// Compact controllers use the existing registered HTTPS client; test transports implement the same typed call boundary.
public interface ICompactNodeClient
{
    Task<JsonObject> CallAsync(Tool tool, JsonObject arguments, CancellationToken cancellation);
}

// The controller wraps an existing authenticated connection and never starts a desktop host or creates pairing state.
public sealed class CompactRegisteredNodeClient(NodeTransportClient client) : ICompactNodeClient
{
    public Task<JsonObject> CallAsync(Tool tool, JsonObject arguments, CancellationToken cancellation)
    {
        return client.CallAsync(tool, arguments, cancellation);
    }
}

// Actual window metadata keeps unknown builds/lifetimes unknown until an application binding can be proved.
public sealed record CompactNativeWindow(int ProcessId, string ProcessName, string Build, string Title, long Handle, string? Started,
    CompactWindowKind Kind);

// Use case: the node reports whether a window is the program's main window or a dialog that blocks it, so a reply names which one.
public enum CompactWindowKind
{
    Main,
    Dialog
}

// Use case: stored selectors, workflow documents and forms name a window only by its kind word, never by its caption.
public static class CompactWindowKindWords
{
    // Throws UnreachableException for a kind value this build does not define.
    public static string Text(CompactWindowKind kind)
    {
        return kind switch
        {
            CompactWindowKind.Main => "main",
            CompactWindowKind.Dialog => "dialog",
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    // An unknown word is refused rather than mapped to a default kind, because a guessed kind could target the wrong window.
    public static bool TryParse(string? word, out CompactWindowKind kind)
    {
        kind = CompactWindowKind.Main;
        if (word == "dialog")
        {
            kind = CompactWindowKind.Dialog;
            return true;
        }

        return word == "main";
    }
}

// Use case: observation and input name the application process and, when the caller has one, the native window handle it reads.
public sealed record CompactWindowSelection(int ProcessId, long? Handle);

// Current native capabilities bind node identity, implementation and only the selected permitted action schemas.
public sealed record CompactNativeCapabilities(string Node, string? Version, string? Platform, CompactContractDigest? Implementation,
    CompactActionSchemas? Actions);

// Complete fresh trees retain native snapshot and field metadata privately, before any model-facing compaction.
public sealed record CompactNativeObservation(string Snapshot, IReadOnlyList<CompactTreeNode> Nodes,
    IReadOnlyDictionary<int, UiFieldMetadata> Metadata);

// Ordinary native refusal/unknown effects cross async execution boundaries with their original node outcome and response.
public sealed class CompactNodeException(string message, CompactWorkflowOperationResult? result = null) : Exception(message)
{
    public CompactWorkflowOperationResult? Result { get; } = result;
}
