using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// One compact conversation retains only observed application lifetimes, exposed semantic references and bounded private evidence.
public sealed class CompactGatewayState
{
    // The same words as the JS gateway; a PID that the latest ui_info did not list is refused before any node call.
    public const string UnlistedPidRefusal = "Select a PID from the current ui_info reply before observing or operating it.";

    public Guid Session { get; } = Guid.NewGuid();
    public string? Node
    {
        get; set;
    }
    public string? Version
    {
        get; set;
    }
    public string? Platform
    {
        get; set;
    }
    public JsonNode? FormCapabilities
    {
        get; set;
    }
    public IReadOnlyList<CompactNativeWindow> ObservedWindows { get; set; } = [];
    public CompactUiProjection Projection { get; } = new();
    public CompactResultStore Results { get; } = new();
    public CompactDemonstration? Demonstration
    {
        get; set;
    }
    public int? SelectedProcess
    {
        get; set;
    }
    public CompactNativeObservation? LatestTree
    {
        get; set;
    }
    public long? SelectedWindow
    {
        get; set;
    }
    // The screenshot ui_input returned last; a click must name it, so coordinates never outlive the picture they came from.
    public CompactInputShot? InputShot
    {
        get; set;
    }
    public CompactResultAuthority Authority => new(Node ?? "unknown", Session);

    // Throws CompactNodeException when a caller has not established this process through Info in the current conversation.
    public void RequireObserved(int process)
    {
        if (Node == null || !ObservedWindows.Any(window => window.ProcessId == process))
        {
            throw new CompactNodeException(UnlistedPidRefusal);
        }
    }

    // Throws CompactNodeException for a window that is not current, and ArgumentException when no process is named.
    public CompactWindowSelection SelectWindow(JsonObject arguments, int? fallbackProcess = null)
    {
        long? requestedWindow = WindowArgument(arguments);
        if (requestedWindow is long handle)
        {
            return NamedWindow(handle, ProcessArgument(arguments));
        }
        int? process = ProcessArgument(arguments) ?? fallbackProcess;
        if (process is not int selectedProcess)
        {
            throw new ArgumentException("Select an observed PID.");
        }

        return RememberedWindow(selectedProcess);
    }

    // Throws CompactNodeException when the window is not a current window of the requested process; no node call is made.
    private CompactWindowSelection NamedWindow(long handle, int? process)
    {
        var row = ObservedWindows.FirstOrDefault(window => window.Handle == handle && (process == null || window.ProcessId == process));
        if (row == null)
        {
            throw new CompactNodeException(CompactWindowChoice.UnknownHandleRefusal);
        }
        SelectedWindow = handle;

        return new(row.ProcessId, handle);
    }

    // Throws CompactNodeException when the process was not observed by Info; a remembered window stays only while it is current.
    private CompactWindowSelection RememberedWindow(int process)
    {
        RequireObserved(process);
        bool stillCurrent = SelectedWindow is long handle
            && ObservedWindows.Any(window => window.Handle == handle && window.ProcessId == process);
        if (!stillCurrent)
        {
            SelectedWindow = null;
        }

        return new(process, SelectedWindow);
    }

    // Throws InvalidOperationException for a window that is not an integer handle; an absent window remains absent.
    private static long? WindowArgument(JsonObject arguments)
    {
        if (arguments["window"] == null)
        {
            return null;
        }

        return arguments["window"]!.GetValue<long>();
    }

    // Throws InvalidOperationException for a process that is not an integer; an absent process remains absent.
    private static int? ProcessArgument(JsonObject arguments)
    {
        if (arguments["pid"] == null)
        {
            return null;
        }

        return arguments["pid"]!.GetValue<int>();
    }
}

// Generic UI steps preserve the action family before the node resolves platform-specific keyboard names at its existing boundary.
public enum CompactUiAction
{
    Click,
    Write,
    Key
}

// UI commands contain volatile observed references and literal inputs; persisted capture strips these runtime values.
public sealed record CompactUiStep(CompactUiAction Action, string? Reference, string? Text, IReadOnlyList<string>? Keys);
