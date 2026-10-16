using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Workflow requests distinguish private inspection, durable execution and verified-definition authoring at the boundary.
public enum CompactWorkflowCommandKind
{
    Run,
    Status,
    Resume,
    Capture,
    Begin,
    Finish
}

// A validated request retains bounded wire data until its specific typed workflow/parameter decoder runs.
public sealed record CompactWorkflowCommand(CompactWorkflowCommandKind Kind, JsonObject Arguments,
    CompactWorkflowJobReference? Job = null);

// Shared descriptors constrain the wire fields; exact native job references and safe revisions are checked before private state is opened.
public static class CompactWorkflowCommandCodec
{
    // Throws ArgumentException for unknown commands, malformed fields or unbounded/native-invalid references.
    public static CompactWorkflowCommand Read(string name, JsonObject arguments)
    {
        var descriptor = CompactGatewayFunctionTools.Find(name);
        if (descriptor == null)
        {
            throw new ArgumentException("Unknown workflow command.");
        }
        Actions.ToolContract.Validate(descriptor["inputSchema"]!.AsObject(), arguments);
        var kind = Kind(name);
        foreach (string field in new[] { "revision", "expected_revision" })
        {
            if (arguments.ContainsKey(field) && !CompactFunctionDocumentCodec.TryInteger(arguments[field], out _))
            {
                throw new ArgumentException("Workflow revisions require safe positive integers.");
            }
        }
        CompactWorkflowJobReference? reference = null;
        if (kind is CompactWorkflowCommandKind.Status or CompactWorkflowCommandKind.Resume)
        {
            if (!CompactContractHash.TryDigest(arguments["job_ref"]!.ToString(), out var digest))
            {
                throw new ArgumentException("Use an opaque native workflow job reference.");
            }
            reference = new(digest!);
        }

        return new(kind, arguments, reference);
    }

    // Throws ArgumentException for an unsupported wire name; names are converted once before internal routing.
    private static CompactWorkflowCommandKind Kind(string name)
    {
        return name switch
        {
            "workflow.run" => CompactWorkflowCommandKind.Run,
            "workflow.status" => CompactWorkflowCommandKind.Status,
            "workflow.resume" => CompactWorkflowCommandKind.Resume,
            "Programming.workflow_capture" => CompactWorkflowCommandKind.Capture,
            "Programming.workflow_begin" => CompactWorkflowCommandKind.Begin,
            "Programming.workflow_finish" => CompactWorkflowCommandKind.Finish,
            _ => throw new ArgumentException("Unknown workflow command.")
        };
    }
}
