using System.Diagnostics;
using System.Reflection;
using System.Windows.Forms;
using KLaniBot;
using KLaniBot.Guard;
using KLaniBot.Platforms;
using KLaniBot.Platforms.Windows;

// Use case: the Windows backend (UI Automation) and its modal-dialog handling are only compiled on the Linux
// development machine. This program checks them on a real Windows desktop with one command, on a hosted CI runner
// or on any Windows PC with a signed-in, unlocked session. Started without arguments, it launches itself as the
// fixture (a main window and a modal "Zertifikat prüfen" dialog), drives the real UiAutomationService against it
// and ends with "N windows dialog regression checks passed." and exit code 0, otherwise with the first failure
// and exit code 1. It mirrors the Linux ModalDialogRegression, so both platforms are held to the same rule.
internal static class Program
{
    private const string MainTitle = "k-lani-bot Windows-Fixture";
    private const string DialogTitle = "Zertifikat prüfen";
    private const string CountButton = "Count click";
    private const string OpenDialogButton = "Open certificate dialog";
    private const string AcceptButton = "Zertifikat annehmen";
    private const string RejectButton = "Ablehnen";
    private const string TextField = "Fixture text";
    private const string ModalTargetText = ModalDialogPolicy.StopForModalTargetMessage;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "fixture")
        {
            Fixture.Run();
            return 0;
        }
        if (args.Length == 1 && args[0] == "escape")
        {
            return EscapeProbe.Run();
        }
        try
        {
            int passed = new Regression().Run();
            Console.WriteLine(passed + " windows dialog regression checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    // The window under test. The click counter is part of the title, because the title is what ListWindows reports
    // without any further UI Automation call, so a blocked click can be told from an executed one.
    private static class Fixture
    {
        public static void Run()
        {
            Application.EnableVisualStyles();
            int clicks = 0;
            var main = new Form { Text = MainTitle + " · 0", Width = 520, Height = 260 };
            var count = new Button { Text = CountButton, Left = 20, Top = 20, Width = 200 };
            var open = new Button { Text = OpenDialogButton, Left = 20, Top = 60, Width = 200 };
            var text = new TextBox { AccessibleName = TextField, Left = 20, Top = 100, Width = 300 };
            count.Click += (_, _) => main.Text = MainTitle + " · " + ++clicks;
            open.Click += (_, _) => ShowCertificateDialog(main);
            main.Controls.AddRange([count, open, text]);
            Application.Run(main);
        }

        private static void ShowCertificateDialog(Form owner)
        {
            using var dialog = new Form { Text = DialogTitle, Width = 360, Height = 160, StartPosition = FormStartPosition.CenterParent };
            var accept = new Button { Text = AcceptButton, Left = 20, Top = 40, Width = 140, DialogResult = DialogResult.OK };
            var reject = new Button { Text = RejectButton, Left = 180, Top = 40, Width = 140, DialogResult = DialogResult.Cancel };
            dialog.Controls.AddRange([accept, reject]);
            dialog.ShowDialog(owner);
        }
    }

    // Drives the real Windows backend against the fixture window started as a second process, and counts the passed checks.
    private sealed class Regression
    {
        private readonly UiAutomationService _ui = new();
        private Process? _fixture;
        private int _passed;

        public int Run()
        {
            _fixture = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "fixture") { UseShellExecute = false })!;
            try
            {
                CheckWindowWithoutDialog();
                CheckBackgroundText();
                OpenDialogWithoutBlocking();
                CheckTargetBehindDialog();
                CheckTargetInsideDialog();
                CloseDialogAndContinue();
            }
            finally
            {
                if (!_fixture.HasExited)
                {
                    _fixture.Kill(true);
                }
            }
            return _passed;
        }

        private void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new Exception("Windows dialog: " + message);
            }
            _passed++;
        }

        private void CheckWindowWithoutDialog()
        {
            WaitFor(CountButton, MainTitle);
            Check(Refusal(CountButton, MainTitle, ModalDialogMode.Pause, false) == null, "a window without a dialog is not blocked");
        }

        // Background mode replaces a field's text through the ValuePattern, without the shared keyboard.
        private void CheckBackgroundText()
        {
            VisualTreeNode field = WaitFor(TextField, MainTitle);
            var (written, message) = _ui.SetGuardedText(field.Id, "Hallo Windows");
            Check(written, "the field accepts text through the ValuePattern: " + message);
            Check(WaitFor(TextField, MainTitle).Value == "Hallo Windows", "the field shows the text that was set");
        }

        // A button that opens a modal dialog. UI Automation's Invoke can wait until the dialog is closed when the
        // button's handler runs the dialog's message loop; the actuator would then hang. It must come back.
        private void OpenDialogWithoutBlocking()
        {
            VisualTreeNode open = WaitFor(OpenDialogButton, MainTitle);
            Task<UiOutcome> invoke = Task.Run(() => _ui.InvokeBackgroundElement(open.Id));
            bool returned = invoke.Wait(TimeSpan.FromSeconds(5));
            Check(returned, "invoking a button that opens a modal dialog returns instead of waiting for it");
            Check(invoke.Result.Success, "the button opens the certificate dialog: " + invoke.Result.Message);
            WaitFor(AcceptButton, DialogTitle);
        }

        private void CheckTargetBehindDialog()
        {
            string titleBefore = MainWindowTitle();
            Check(Refusal(CountButton, MainTitle, ModalDialogMode.Pause, false) != null, "the main window is refused while dialogs pause");
            Check(
                Refusal(CountButton, MainTitle, ModalDialogMode.AllowInteraction, true) != null,
                "the main window behind the dialog is refused although dialogs are allowed and a decision was recorded");
            Check(MainWindowTitle() == titleBefore, "nothing in the blocked window ran");
        }

        private void CheckTargetInsideDialog()
        {
            string? paused = Refusal(AcceptButton, DialogTitle, ModalDialogMode.Pause, false);
            bool pausedForDialog = paused != null && paused.Contains(ModalTargetText, StringComparison.Ordinal);
            Check(pausedForDialog, "an element in the dialog waits while dialogs pause");
            string? withoutDecision = Refusal(AcceptButton, DialogTitle, ModalDialogMode.AllowInteraction, false);
            Check(
                withoutDecision != null && withoutDecision.Contains(ModalTargetText, StringComparison.Ordinal),
                "an element in the dialog needs the recorded decision");
            Check(
                Refusal(AcceptButton, DialogTitle, ModalDialogMode.AllowInteraction, true) == null,
                "an element in the dialog is allowed with dialogs enabled and a decision");
        }

        private void CloseDialogAndContinue()
        {
            VisualTreeNode reject = WaitFor(RejectButton, DialogTitle);
            var (closed, message) = _ui.InvokeBackgroundElement(reject.Id);
            Check(closed, "the dialog is closed through its own button: " + message);
            WaitUntilWindowGone(DialogTitle);
            bool mainWindowFree = Refusal(CountButton, MainTitle, ModalDialogMode.Pause, false) == null;
            Check(mainWindowFree, "the main window works again once the dialog is closed");
            VisualTreeNode count = WaitFor(CountButton, MainTitle);
            var (clicked, clickMessage) = _ui.InvokeBackgroundElement(count.Id);
            Check(clicked && WaitForTitle(MainTitle + " · 1"), "a click in the background reaches the main window: " + clickMessage);
        }

        // Returns null when the action is allowed, otherwise the refusal message.
        private string? Refusal(string name, string windowTitle, ModalDialogMode dialogs, bool decisionRecorded)
        {
            VisualTreeNode element = Find(name, windowTitle) ?? throw new Exception("Windows dialog: '" + name + "' not found.");
            try
            {
                _ui.PrepareElementInteraction(element.Id, WindowInteractionMode.BackgroundSemantic, dialogs, decisionRecorded);
                return null;
            }
            catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException)
            {
                return exception.Message;
            }
        }

        // Every observation replaces the element ids, so ids are used straight after the call that returned them.
        private VisualTreeNode? Find(string name, string windowTitle)
        {
            object? tree = _ui.GetVisualTree(_fixture!.Id, windowTitle, 8);
            if (tree?.GetType().GetProperty("root", BindingFlags.Public | BindingFlags.Instance)?.GetValue(tree) is not VisualTreeNode root)
            {
                return null;
            }
            return Flatten(root).FirstOrDefault(node => node.Name == name);
        }

        private static IEnumerable<VisualTreeNode> Flatten(VisualTreeNode node)
        {
            yield return node;
            foreach (VisualTreeNode child in node.Children)
            {
                foreach (VisualTreeNode inner in Flatten(child))
                {
                    yield return inner;
                }
            }
        }

        private VisualTreeNode WaitFor(string name, string windowTitle)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    VisualTreeNode? found = Find(name, windowTitle);
                    if (found != null)
                    {
                        return found;
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
                {
                    // The window is still being created, or for a moment two windows match the title.
                }
                Thread.Sleep(100);
            }
            throw new Exception("Windows dialog: '" + name + "' never appeared in '" + windowTitle + "'.");
        }

        private string MainWindowTitle()
        {
            WindowInfo main = _ui.ListWindows().First(window =>
                window.ProcessId == _fixture!.Id && window.Title.StartsWith(MainTitle, StringComparison.Ordinal));
            return main.Title;
        }

        private bool WaitForTitle(string title)
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                if (MainWindowTitle() == title)
                {
                    return true;
                }
                Thread.Sleep(100);
            }
            return false;
        }

        private void WaitUntilWindowGone(string title)
        {
            for (int attempt = 0; attempt < 100; attempt++)
            {
                if (!_ui.ListWindows().Any(window => window.ProcessId == _fixture!.Id && window.Title == title))
                {
                    return;
                }
                Thread.Sleep(100);
            }
            throw new Exception("Windows dialog: '" + title + "' never closed.");
        }
    }
}

// Use case: the person at a Windows machine checks once that the ESC watch sees their own key and ignores a key a program
// sends, the way the node types. Started with "escape"; it sends one ESC itself, then asks for a real one.
internal static class EscapeProbe
{
    private const byte EscapeVirtualKey = 0x1B;
    private const uint KeyUp = 0x0002;
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PersonTime = TimeSpan.FromSeconds(30);

    public static int Run()
    {
        if (!KLaniBot.Visibility.WindowsEscapeKeyWatch.TryStart(out var watch))
        {
            Console.Error.WriteLine("FAILED: the keyboard hook was refused.");
            return 1;
        }
        using (watch)
        {
            keybd_event(EscapeVirtualKey, 0, 0, UIntPtr.Zero);
            keybd_event(EscapeVirtualKey, 0, KeyUp, UIntPtr.Zero);
            if (SeesEscape(watch, SettleTime))
            {
                Console.Error.WriteLine("FAILED: an ESC sent by a program counted as the person's key.");
                return 1;
            }
            Console.WriteLine("Sent ESC ignored. Please press ESC once now (30 seconds).");
            if (!SeesEscape(watch, PersonTime))
            {
                Console.Error.WriteLine("FAILED: no ESC from the keyboard was seen.");
                return 1;
            }
        }
        Console.WriteLine("Escape probe passed: the keyboard's ESC counts, a sent ESC does not.");
        return 0;
    }

    private static bool SeesEscape(KLaniBot.Visibility.WindowsEscapeKeyWatch watch, TimeSpan time)
    {
        var waited = Stopwatch.StartNew();
        while (waited.Elapsed < time)
        {
            if (watch.TakePhysicalEscape())
            {
                return true;
            }
            Thread.Sleep(50);
        }
        return false;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
