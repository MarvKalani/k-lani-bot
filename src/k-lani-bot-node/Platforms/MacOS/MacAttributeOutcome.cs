namespace KLaniBot.Platforms.MacOS;

// The outcome of reading one accessibility attribute. The macOS tree reader copies a value, treats a missing one as
// empty, and refuses the whole observation for a failed read, so each attribute read ends in exactly one of these.
public enum MacAttributeOutcome
{
    Value,
    Absent,
    Failed
}
