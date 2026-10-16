using System.Text.Json.Nodes;

namespace KLaniBot.Transport.CompactUi;

// Compatibility routing separates non-mutating assessment from receipt-bound active revision replacement.
public enum CompactCompatibilityCommandKind
{
    Info,
    Revalidate
}

// Only opaque receipt references and exact revisions reach revalidation; request-supplied success flags cannot approve functions.
public sealed record CompactCompatibilityCommand(CompactCompatibilityCommandKind Kind, string Function,
    long? Revision = null, CompactAcceptanceReference? Evidence = null);

// The native boundary enforces the shared bounded compatibility contract before looking up a selected application or receipt.
public static class CompactCompatibilityCommandCodec
{
    private const int MaximumArgumentLength = 128;

    public static bool TryRead(string name, JsonObject arguments, out CompactCompatibilityCommand? command)
    {
        command = null;
        if (!Text(arguments, "function", out string function))
        {
            return false;
        }
        if (name == "Info.compatibility" && CompactDiscoveryDocumentCodec.Only(arguments, "function"))
        {
            command = new(CompactCompatibilityCommandKind.Info, function);
            return true;
        }
        if (name != "Programming.revalidate" || !CompactDiscoveryDocumentCodec.Only(arguments,
                "function", "expected_revision", "evidence_ref")
            || !CompactFunctionDocumentCodec.TryInteger(arguments["expected_revision"], out long revision)
            || !Text(arguments, "evidence_ref", out string evidence) || !Guid.TryParseExact(evidence, "D", out Guid reference)
            || reference == Guid.Empty || reference.ToString() != evidence)
        {
            return false;
        }
        command = new(CompactCompatibilityCommandKind.Revalidate, function, revision, new(reference));

        return true;
    }

    private static bool Text(JsonObject arguments, string name, out string value)
    {
        return CompactDiscoveryDocumentCodec.Text(arguments, name, out value) && value.Length is >= 1 and <= MaximumArgumentLength;
    }
}
