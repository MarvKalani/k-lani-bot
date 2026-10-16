namespace KLaniBot.Inventory;

// Whether a listed program is something a person operates or a part that other programs need.
public enum ApplicationKind
{
    Application,
    Component
}

// One installed program as a platform source reports it. "Identifiers" holds the technical names that
// help recognise it (package name, desktop entry id, bundle id, executable name); the list is not shown
// to a person but compared with the hints of the target catalogue.
// "Location" is where the program lives when that is known and not obvious (an AppImage, a manual install,
// a bundle); an administrator uses it to find the files of a program the package manager does not know.
public sealed record ApplicationEvidence(
    string Name,
    string Version,
    string Publisher,
    string Source,
    string[] Identifiers,
    ApplicationKind Kind = ApplicationKind.Application,
    string Location = "");

// Use case: an administrator wants to know what is installed on a machine. Each operating system has its
// own place for that answer, so each gets a source behind this interface; everything after it (matching
// with cartridges, the report) is the same for all of them.
public interface IApplicationSource
{
    string OsFamily
    {
        get;
    }

    // executableHints: executable names the caller would like to be found even when they are not listed
    // as installed software (on Windows an Office program is registered only through its App Paths entry).
    IReadOnlyList<ApplicationEvidence> Enumerate(IReadOnlyCollection<string> executableHints);

    // Short statements about what this source could not see, for the report. A scan is only as complete as its sources.
    IReadOnlyList<string> Limitations
    {
        get;
    }
}
