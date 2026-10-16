#if WINDOWS
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows;

namespace KLaniBot.Platforms.Windows;

// Long text reaches an editor through the clipboard instead of keystrokes; the person's clipboard is put back afterwards and the paste must
// be confirmed by the editor.
internal static class WindowsClipboardPaste
{
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    // Throws InvalidOperationException when the paste fails, with the cause as inner exception.
    public static void Paste(AutomationElement target, string text, bool clear)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                PasteOnStaThread(target, text, clear);
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
        {
            throw new InvalidOperationException("Clipboard text insertion failed: " + failure.Message, failure);
        }
    }

    // Throws UnauthorizedAccessException for password fields and InvalidOperationException when focus or clipboard change, or the editor
    // does not confirm.
    private static void PasteOnStaThread(AutomationElement target, string text, bool clear)
    {
        if (target.Current.IsPassword)
        {
            throw new UnauthorizedAccessException("Clipboard insertion is not used for password fields.");
        }
        int[] identity = target.GetRuntimeId();
        target.SetFocus();
        RequireFocus(identity);
        if (clear)
        {
            WindowsInput.Chord(["ctrl", "a"]);
            RequireFocus(identity);
        }
        // Keep the original OLE data object locally, including its available formats.
        // It is never serialized into MCP responses or persisted on disk.
        IDataObject? previous = Clipboard.GetDataObject();
        DataObject temporary = TemporaryClipboardData(text);
        uint ourSequence = 0;
        using var changed = new ManualResetEventSlim();
        AutomationEventHandler onChange = SignalOnChange(changed);
        bool subscribed = false;
        try
        {
            Automation.AddAutomationEventHandler(TextPattern.TextChangedEvent, target, TreeScope.Element, onChange);
            subscribed = true;
            SetClipboardData(temporary);
            ourSequence = GetClipboardSequenceNumber();
            PasteWhenUnchanged(identity, ourSequence, changed);
        }
        finally
        {
            if (subscribed)
            {
                Automation.RemoveAutomationEventHandler(TextPattern.TextChangedEvent, target, onChange);
            }
            RestoreClipboard(previous, ourSequence);
        }
    }

    // Throws InvalidOperationException when the input desktop or the focused element changed.
    private static void RequireFocus(int[] identity)
    {
        WindowsDesktopSession.Read().RequireInputAvailable();
        if (!identity.SequenceEqual(AutomationElement.FocusedElement.GetRuntimeId()))
        {
            throw new InvalidOperationException("Focus changed; no further input was sent.");
        }
    }

    // Windows line endings, and marked so that neither clipboard history nor cloud sync keeps a copy.
    private static DataObject TemporaryClipboardData(string text)
    {
        var temporary = new DataObject();
        temporary.SetData(
            DataFormats.UnicodeText,
            text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal), false);
        temporary.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]), false);
        temporary.SetData("CanUploadToCloudClipboard", new MemoryStream(new byte[4]), false);
        return temporary;
    }

    private static AutomationEventHandler SignalOnChange(ManualResetEventSlim changed)
    {
        return (_, _) =>
        {
            try
            {
                changed.Set();
            }
            catch (ObjectDisposedException)
            {
                // UIA may deliver an already queued callback after removal.
            }
        };
    }

    private static readonly TimeSpan PasteAcknowledgeTimeout = TimeSpan.FromSeconds(2);

    // Throws InvalidOperationException when focus or clipboard changed before the paste, or the editor does not confirm it.
    private static void PasteWhenUnchanged(int[] identity, uint ourSequence, ManualResetEventSlim changed)
    {
        RequireFocus(identity);
        if (ourSequence != GetClipboardSequenceNumber())
        {
            throw new InvalidOperationException("Clipboard changed before paste; operation cancelled.");
        }
        WindowsInput.Chord(["ctrl", "v"]);
        // Ctrl+V queues input. Restoring immediately can replace the data before
        // the editor consumes it; wait for its text-change event first.
        if (!changed.Wait(PasteAcknowledgeTimeout))
        {
            throw new InvalidOperationException("Editor did not acknowledge the paste; verify the result before another write.");
        }
    }

    // Throws ExternalException when Windows keeps the clipboard locked after the bounded retries.
    private static void SetClipboardData(object data)
    {
        const int MaximumAttempts = 5;
        const int RetryMilliseconds = 20;
        for (int attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return;
            }
            catch (ExternalException) when (attempt + 1 < MaximumAttempts)
            {
                Thread.Sleep(RetryMilliseconds);
            }
        }
    }

    // Only a clipboard that still holds our data is restored; anything copied since then is left alone.
    private static void RestoreClipboard(IDataObject? previous, uint ourSequence)
    {
        if (ourSequence == 0 || GetClipboardSequenceNumber() != ourSequence)
        {
            return;
        }
        if (previous == null)
        {
            Clipboard.Clear();
        }
        else
        {
            SetClipboardData(previous);
        }
    }
}
#endif
