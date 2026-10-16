using System.Diagnostics;

namespace KLaniBot.Transport.CompactUi;

// Definition and recovery failures preserve explicit legacy protocol messages instead of treating unreadable state as an empty library.
public static class CompactFunctionNames
{
    public static string Error(CompactFunctionError error, string? detail = null)
    {
        return error switch
        {
            CompactFunctionError.InvalidDocument => "Generated cartridge identity or format does not match.",
            CompactFunctionError.InvalidDefinition => "Invalid generated function definition.",
            CompactFunctionError.DuplicateFunction => "Duplicate generated function.",
            CompactFunctionError.DuplicateOutput => "Duplicate generated output.",
            CompactFunctionError.InvalidParameters => "Invalid generated parameter schema.",
            CompactFunctionError.InvalidAction => "Unsupported generated action or route.",
            CompactFunctionError.InvalidSelector => "Generated selectors require an owning window and contain no IDs or coordinates.",
            CompactFunctionError.InvalidSemanticSelector => "Generated selector is not semantic.",
            CompactFunctionError.InvalidContainerPath => "Invalid semantic container path.",
            CompactFunctionError.InvalidWriteParameter => "Generated write input is not parameterized.",
            CompactFunctionError.InvalidOutput => "Invalid generated output check.",
            CompactFunctionError.UnknownOutputParameter => "Output check refers to an unknown parameter.",
            CompactFunctionError.InvalidCandidate => "Invalid generated candidate.",
            CompactFunctionError.InvalidRevision => "Invalid cartridge revision.",
            CompactFunctionError.InvalidPath => "Use separate absolute cartridge and private recovery paths.",
            CompactFunctionError.ByteLimit => "Cartridge exceeds its byte limit.",
            CompactFunctionError.UnsafeFile => "Invalid or symbolic cartridge state.",
            CompactFunctionError.StorageUnavailable => detail ?? "Generated cartridge storage is unavailable.",
            CompactFunctionError.Locked => "cartridge_revision_locked",
            CompactFunctionError.StaleRevision => "stale_cartridge_revision",
            CompactFunctionError.InvalidJournal => "recoverable_invalid_revision_journal",
            CompactFunctionError.ExternalChange => "recoverable_external_cartridge_change",
            CompactFunctionError.CorruptTemporary => "recoverable_corrupt_temporary",
            CompactFunctionError.AlreadyExists => "Function already exists; it was not overwritten.",
            _ => throw new UnreachableException()
        };
    }
}
