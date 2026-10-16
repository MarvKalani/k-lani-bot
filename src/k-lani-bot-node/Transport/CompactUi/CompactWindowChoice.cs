using System.Diagnostics.CodeAnalysis;

namespace KLaniBot.Transport.CompactUi;

// Use case: before the node is asked for a tree, the caller proves which one current window it belongs to, so each row gets
// that window's kind. The rule follows the node's own selection: a named handle, else the only window, else the only dialog.
public static class CompactWindowChoice
{
    public const string AmbiguousRefusal = "Recipe window is absent, foreign or ambiguous; name the main window with the window "
        + "argument (the native handle from ui_info).";

    public const string UnknownHandleRefusal = "The window handle is not one of the current windows; call ui_info again.";

    public const string DemonstrationWindowRefusal = "The window the demonstration used is no longer current; observe it again.";

    public const string UniqueWindowRefusal = "Select one unambiguous current application window for learning/reuse.";

    public const string ProcessInstanceRefusal = "The application process is absent or its window lifetime is unknown; call ui_info again.";

    public const string ReferenceWindowRefusal = "The observed reference belongs to another window; select its window "
        + "with the window argument.";

    // Returns false when no single window is proved; the caller refuses before any observation or input.
    public static bool TryChoose(IReadOnlyList<CompactNativeWindow> current, CompactWindowSelection selection,
        [NotNullWhen(true)] out CompactNativeWindow? window)
    {
        window = null;
        CompactNativeWindow[] ofProcess = current.Where(candidate => candidate.ProcessId == selection.ProcessId).ToArray();
        if (selection.Handle is long handle)
        {
            window = ofProcess.FirstOrDefault(candidate => candidate.Handle == handle);
            return window != null;
        }
        if (ofProcess.Length == 1)
        {
            window = ofProcess[0];
            return true;
        }
        CompactNativeWindow[] dialogs = ofProcess.Where(candidate => candidate.Kind == CompactWindowKind.Dialog).ToArray();
        if (dialogs.Length == 1)
        {
            window = dialogs[0];
            return true;
        }

        return false;
    }

    // A recipe's process instance needs one current window of the process only to prove identity; each step then binds its own kind.
    // A named handle must be current, so an unknown handle is never replaced by another window of the same process.
    public static bool TryProcessInstance(IReadOnlyList<CompactNativeWindow> current, CompactWindowSelection selection,
        [NotNullWhen(true)] out CompactNativeWindow? window)
    {
        CompactNativeWindow[] ofProcess = current.Where(candidate => candidate.ProcessId == selection.ProcessId).ToArray();
        if (selection.Handle is long handle)
        {
            window = ofProcess.FirstOrDefault(candidate => candidate.Handle == handle);
            return window != null;
        }
        window = ofProcess.FirstOrDefault();

        return window != null;
    }
}
