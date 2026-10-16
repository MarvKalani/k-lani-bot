using System.Text.Json.Serialization;

namespace KLaniBot.Platforms;

// Use case: a window action names its window with one request, and every backend selects that window through Select, so
// the same request chooses the same window on each platform. A native handle names exactly one window; a process and a
// caption name one only when that leaves a single window, or a single dialog that blocks its main window.
public sealed record WindowSelectionRequest(int? ProcessId, string? TitleQuery, long? Handle);

// Use case: the outcome of one selection, so that a caller returns the window, reports that none matches, or refuses.
public enum WindowSelectionOutcome
{
    Chosen,
    NoMatch,
    Ambiguous,
    UnknownHandle
}

// Use case: what Select found, with the number of windows it considered, so that the error names the real count.
public sealed record WindowSelectionResult(WindowInfo? Window, WindowSelectionOutcome Outcome, int Candidates);

// Use case: list_windows tells an AI client whether a window is a main window or a dialog, so a process alone can reach
// the dialog that blocks its main window. Only the words are read, never a position.
[JsonConverter(typeof(WindowKindJsonConverter))]
public enum WindowKind
{
    [JsonStringEnumMemberName("main")]
    Main,

    [JsonStringEnumMemberName("dialog")]
    Dialog
}

// A number in place of a word would name whatever kind has that position today, so only words are read.
public sealed class WindowKindJsonConverter() : JsonStringEnumConverter<WindowKind>(namingPolicy: null, allowIntegerValues: false);

// Use case: the one selection rule that every backend and every window action shares, so no platform guesses differently.
public static class WindowSelection
{
    // Marks a position in a window list where no window was found.
    public const int NoWindow = -1;

    // A request without a process, caption or handle names no window; the caller then takes the foreground window itself.
    public static bool HasCriteria(WindowSelectionRequest request)
    {
        return request.ProcessId.HasValue || !string.IsNullOrEmpty(request.TitleQuery) || request.Handle.HasValue;
    }

    // Callers answer a request without criteria, such as the foreground window, before they select.
    public static WindowSelectionResult Select(IReadOnlyList<WindowInfo> windows, WindowSelectionRequest request)
    {
        if (request.Handle.HasValue)
        {
            return SelectByHandle(windows, request.Handle.Value, request.ProcessId);
        }
        if (!HasCriteria(request))
        {
            return new(null, WindowSelectionOutcome.NoMatch, 0);
        }

        return SelectByProcessAndTitle(windows, request);
    }

    // Throws ArgumentException when several windows match, or when the native handle names no window, so the caller says why.
    public static WindowInfo? RequireChoice(WindowSelectionRequest request, WindowSelectionResult result)
    {
        if (result.Outcome == WindowSelectionOutcome.Ambiguous)
        {
            throw new ArgumentException(AmbiguityMessage(request.ProcessId, result.Candidates));
        }
        if (result.Outcome == WindowSelectionOutcome.UnknownHandle)
        {
            throw new ArgumentException("No window with this native handle exists. Read list_windows again.");
        }

        return result.Window;
    }

    // An accessibility name identifies a window by its caption: an exact caption wins, and a caption that only contains the
    // name counts when it is the single candidate. Several exact captions stay ambiguous rather than falling back.
    // An empty name belongs to the process's untitled window, but only when it has exactly one.
    public static int IndexOfNamedWindow(IReadOnlyList<WindowInfo> windows, int processId, string accessibleName)
    {
        if (string.IsNullOrEmpty(accessibleName))
        {
            return IndexOfSingleUntitledWindow(windows, processId);
        }

        var exact = IndicesOf(windows, processId, window => window.Title == accessibleName);
        var candidates = exact.Count > 0
            ? exact
            : IndicesOf(windows, processId, window => window.Title.Contains(accessibleName, StringComparison.OrdinalIgnoreCase));

        return candidates.Count == 1 ? candidates[0] : NoWindow;
    }

    // The process's untitled window, or NoWindow when it has none or several.
    public static int IndexOfSingleUntitledWindow(IReadOnlyList<WindowInfo> windows, int processId)
    {
        var untitled = IndicesOf(windows, processId, window => window.Title.Length == 0);

        return untitled.Count == 1 ? untitled[0] : NoWindow;
    }

    // The title is ignored: the handle already names one window, and a process given with it must own that window.
    private static WindowSelectionResult SelectByHandle(IReadOnlyList<WindowInfo> windows, long handle, int? processId)
    {
        var matches = windows.Where(window => window.WindowHandle == handle
            && (!processId.HasValue || window.ProcessId == processId.Value)).ToList();

        if (matches.Count == 1)
        {
            return new(matches[0], WindowSelectionOutcome.Chosen, 1);
        }
        if (matches.Count > 1)
        {
            return new(null, WindowSelectionOutcome.Ambiguous, matches.Count);
        }

        return new(null, WindowSelectionOutcome.UnknownHandle, 0);
    }

    // A showing dialog blocks its main window, so when exactly one match is a dialog, input belongs to it.
    private static WindowSelectionResult SelectByProcessAndTitle(IReadOnlyList<WindowInfo> windows, WindowSelectionRequest request)
    {
        var matches = windows.Where(window => MatchesProcess(window, request.ProcessId)
            && MatchesTitle(window, request.TitleQuery)).ToList();
        if (matches.Count == 0)
        {
            return new(null, WindowSelectionOutcome.NoMatch, 0);
        }
        if (matches.Count == 1)
        {
            return new(matches[0], WindowSelectionOutcome.Chosen, 1);
        }

        var dialogs = matches.Where(window => window.Kind == WindowKind.Dialog).ToList();
        if (dialogs.Count == 1)
        {
            return new(dialogs[0], WindowSelectionOutcome.Chosen, matches.Count);
        }

        return new(null, WindowSelectionOutcome.Ambiguous, matches.Count);
    }

    private static bool MatchesProcess(WindowInfo window, int? processId)
    {
        return !processId.HasValue || window.ProcessId == processId.Value;
    }

    private static bool MatchesTitle(WindowInfo window, string? titleQuery)
    {
        return string.IsNullOrEmpty(titleQuery) || window.Title.Contains(titleQuery, StringComparison.OrdinalIgnoreCase);
    }

    private static List<int> IndicesOf(IReadOnlyList<WindowInfo> windows, int processId, Func<WindowInfo, bool> isNamed)
    {
        var indices = new List<int>();
        for (int index = 0; index < windows.Count; index++)
        {
            if (windows[index].ProcessId == processId && isNamed(windows[index]))
            {
                indices.Add(index);
            }
        }

        return indices;
    }

    // The one message for an ambiguous selection on every platform; it names the process only when the request has one.
    public static string AmbiguityMessage(int? processId, int candidates)
    {
        const string Hint = "select one with the window argument (the native handle from list_windows).";
        if (processId.HasValue)
        {
            return $"Window selector is ambiguous: process {processId.Value} has {candidates} matching windows; {Hint}";
        }

        return $"Window selector is ambiguous: {candidates} windows match the title; {Hint}";
    }
}
