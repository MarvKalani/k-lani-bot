namespace KLaniBot.Common;

// Why reading a file the operator controls failed; it decides how the failure is described.
public enum ReadProblemKind
{
    // The file is missing or cannot be read.
    Unreadable,

    // The content is not JSON of the expected shape, or names a word the node does not know.
    NotValid,

    // A value that must be there is missing.
    Missing,

    // A value is present but breaks a rule, for example a port outside 1 to 65535.
    Refused
}

// Why a file from outside could not be read. Detail names the file or the value, never its secret content.
public sealed record ReadProblem(ReadProblemKind Kind, string Detail)
{
    public string Describe()
    {
        return Kind + ": " + Detail;
    }
}
