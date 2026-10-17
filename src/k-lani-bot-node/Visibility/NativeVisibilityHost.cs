using System.Collections.Concurrent;
using System.Diagnostics;
using KLaniBot.Guard;
using KLaniBot.Localization;
using KLaniBot.Startup;

namespace KLaniBot.Visibility;

// The shared part of the operated node's tray, border and consent dialog on each desktop platform. The tray menu
// is how the person at the machine answers consent and changes local settings; every choice goes to
// AccessVisibility, which decides whether it is accepted while an action runs.
//
// All native GUI calls run on the executable's initial thread, and other threads hand work to it through
// NativeCall. AppKit in particular must never be initialized from the desktop actor or a thread-pool callback.
public abstract class NativeVisibilityHost : IVisibilityDisplay
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    // Work queued for the UI thread and whether it started, so a cancelled caller does not wait for work that never ran.
    private sealed class Pending(Action work)
    {
        public Action Work { get; } = work;
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int State; // 0 queued, 1 started, 2 cancelled before start
    }
    private readonly ConcurrentQueue<Pending> _pending = new();
    private bool _disposed; private string? _shownPrompt;
    private bool _stopRequested;
    protected string NodeId { get; private set; } = "";
    protected string ShownNodeId => NodeId.Length > 0 ? NodeId : UiTexts.Get(UiText.NotStated);

    // The master of the connected client, or a note that no client is connected yet.
    protected static string ShownMasterOf(VisibilityState state)
    {
        return state.Presentation?.ShownMaster ?? UiTexts.Get(UiText.MenuNoClientConnected);
    }
    private string? _traceDirectory;

    public void SetNodeStateDirectory(string stateRoot)
    {
        _traceDirectory = Path.Combine(Path.GetFullPath(stateRoot), "action-traces");
    }
    protected void OpenTraceViewer()
    {
        if (_traceDirectory == null || !Directory.Exists(_traceDirectory))
        {
            return;
        }
        string viewer = Path.Combine(_traceDirectory, "index.html");
        if (File.Exists(viewer) && (File.GetAttributes(viewer) & FileAttributes.ReparsePoint) == 0)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = viewer,
                UseShellExecute = true
            });
        }
    }
    protected bool HasTraceViewer => _traceDirectory != null && File.Exists(Path.Combine(_traceDirectory, "index.html"));
    // Throws ArgumentException for an invalid node id.
    public void SetNodeIdentity(string nodeId)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || nodeId.Length > 80 || nodeId.Any(char.IsControl))
        {
            throw new ArgumentException("Invalid visible node identity.");
        }
        NodeId = nodeId;
    }
    protected AccessVisibility? Controller
    {
        get; private set;
    }
    protected VisibilityState Current { get; private set; } = new(VisibilityStatus.Idle, 0, false, null, false, true);
    public void Attach(AccessVisibility controller) => Controller = controller;
    protected abstract void Apply(VisibilityState state);
    protected abstract void Pump();
    protected abstract void CloseNative();
    protected abstract void ShowQuestion(ConsentPrompt? prompt);
    // Null where the desktop cannot tell a physical ESC from a sent one; ESC then ends nothing.
    protected abstract IEscapeKeyWatch? StartEscapeKeyWatch();
    private IEscapeKeyWatch? _escapeWatch;
    protected virtual void Perform(Action action) => action();
    protected void Decide(SessionConsent decision)
    {
        var prompt = Current.Prompt;
        if (prompt != null && Controller?.Decide(prompt.Key, prompt.Challenge, decision) == true)
            ShowQuestion(null);
    }
    protected void SetUnattended() => Controller?.SetUnattended(!Current.Unattended);
    protected void SetBorder() => Controller?.SetBorder(!Current.ScreenBorder);
    protected void SetBackgroundInteraction()
    {
        var mode = Current.WindowInteraction == WindowInteractionMode.Foreground
            ? WindowInteractionMode.BackgroundSemantic : WindowInteractionMode.Foreground;
        Controller?.SetWindowInteraction(mode, Current.ModalDialogs);
    }
    protected void SetModalInteraction()
    {
        var mode = Current.ModalDialogs == ModalDialogMode.Pause
            ? ModalDialogMode.AllowInteraction : ModalDialogMode.Pause;
        Controller?.SetWindowInteraction(Current.WindowInteraction, mode);
    }
    protected void SetScreenshotRecording()
    {
        Controller?.SetScreenshotRecording(!Current.RecordActionScreenshots);
    }
    protected void Revoke() => Controller?.RevokeAll();
    protected void PauseAgent() => Controller?.SetPace(Pace.Paused);
    protected void SlowMotion() => Controller?.SetPace(Pace.SlowMotion);
    protected void NormalSpeed() => Controller?.SetPace(Pace.Normal);

    // One checkable menu item locks and lifts the lock, like the other switches, so the person sees in one place
    // which state the machine is in.
    protected static string AccessLockLabel => UiTexts.Get(UiText.LockAllAccess);

    // The answers the consent dialog offers, in the order it shows them.
    protected sealed record ConsentChoice(UiText Label, SessionConsent Decision);

    protected static readonly ConsentChoice[] ConsentChoices =
    [
        new(UiText.AllowThisSession, SessionConsent.Allowed),
        new(UiText.NotNow, SessionConsent.Denied),
        new(UiText.AskAgainInFiveMinutes, SessionConsent.Deferred)
    ];

    protected void ToggleAccessLock()
    {
        if (Current.AccessLocked)
        {
            Controller?.UnlockAccess();
        }
        else
        {
            Controller?.LockAccess();
        }
    }
    protected void Quit()
    {
        Controller?.RevokeAll();
        _stopRequested = true;
    }
    protected void ShowPending()
    {
        if (Current.Prompt == null)
            Controller?.RequestApprovalLocally();
        ShowQuestion(Current.Prompt);
    }
    public void Render(VisibilityState state)
    {
        Invoke(() =>
        {
            Current = state;
            Apply(state);
            string? prompt = state.Prompt?.Challenge;
            if (prompt != _shownPrompt)
            {
                _shownPrompt = prompt;
                ShowQuestion(state.Prompt);
            }
            Pump();
        });
    }
    public T NativeCall<T>(Func<T> action)
    {
        T result = default!;
        Invoke(() => result = action(), TimeSpan.FromSeconds(35));
        return result;
    }
    protected void Invoke(Action action) => Invoke(action, TimeSpan.FromSeconds(3));
    // Throws InvalidOperationException after the host stopped and TimeoutException when the UI thread does not start the work in time.
    private void Invoke(Action action, TimeSpan timeout)
    {
        if (_disposed)
            throw new InvalidOperationException("Visible host stopped.");
        if (Environment.CurrentManagedThreadId == _thread)
        {
            Perform(action);
            return;
        }
        var call = new Pending(action);
        _pending.Enqueue(call);
        try
        {
            call.Done.Task.WaitAsync(timeout).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            if (Interlocked.CompareExchange(ref call.State, 2, 0) == 0)
                throw new TimeoutException("Native UI did not start the requested operation; queued work cancelled.");
        }
        // Once native work has started, do not return a fictitious not-dispatched
        // result while it can still mutate the OS. The supervisor owns the outer
        // process deadline and unknown outcome for an unresponsive native call.
        call.Done.Task.GetAwaiter().GetResult();
    }
    public int Run(Func<Task<int>> work)
    {
        _escapeWatch = StartEscapeKeyWatch();
        var task = Task.Run(work);
        long lastTick = System.Diagnostics.Stopwatch.GetTimestamp();
        while (!task.IsCompleted && !_stopRequested)
        {
            while (_pending.TryDequeue(out var call))
            {
                if (Interlocked.CompareExchange(ref call.State, 1, 0) != 0)
                {
                    call.Done.TrySetCanceled();
                    continue;
                }
                try
                {
                    Perform(call.Work);
                    call.Done.SetResult();
                }
                catch (Exception ex) { call.Done.SetException(ex); }
            }
            Pump();
            PauseOnEscape();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(lastTick) >= TimeSpan.FromMilliseconds(100))
            {
                lastTick = System.Diagnostics.Stopwatch.GetTimestamp();
                Controller?.Refresh();
            }
            Thread.Sleep(10);
        }
        return _stopRequested ? 0 : task.GetAwaiter().GetResult();
    }
    private void PauseOnEscape()
    {
        bool pressed = _escapeWatch?.TakePhysicalEscape() == true;
        if (pressed && AccessVisibility.EscapePausesAgent(Current))
        {
            Controller?.PauseByEscape();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _escapeWatch?.Dispose();
        while (_pending.TryDequeue(out var call))
            call.Done.TrySetException(new ObjectDisposedException("Visible host"));
        CloseNative();
    }
    public static NativeVisibilityHost Create()
    {
#if WINDOWS
        return new WindowsVisibilityHost();
#else
        return OperatingSystem.IsMacOS() ? new MacVisibilityHost() : new LinuxVisibilityHost();
#endif
    }
    protected static string DescribeAssignment(VisibilityState state)
    {
        return string.Join(Environment.NewLine, AssignmentLines(state));
    }

    protected static List<string> AssignmentLines(VisibilityState state, bool includeSteps = true)
    {
        return AssignmentRows(state, includeSteps).Select(row => row.Text).ToList();
    }

    // The banner's lines with their kind, so a host can set the heading apart from the identity and the steps. At normal pace
    // the step now running, or the last one, follows the identity; while the agent is paused or slowed down the pace and the
    // step list follow it instead.
    protected static List<AssignmentRow> AssignmentRows(VisibilityState state, bool includeSteps = true)
    {
        SessionPresentation details = state.Presentation ?? SessionPresentation.Create(null, null);
        string activity = UiTexts.Get(state.Status == VisibilityStatus.Active ? UiText.AssignmentWorking : UiText.AssignmentGranted);
        // Client and purpose are plain, client-supplied labels. Line breaks must
        // not create additional identity rows in the always-visible indicator.
        string client = details.ShownClient.Replace('\n', ' ');
        string master = details.ShownMaster.Replace('\n', ' ');
        string purpose = details.ShownPurpose.Replace('\n', ' ');
        var lines = new List<AssignmentRow>
        {
            new(ProductIdentity.ProductName + " · " + activity, AssignmentRowKind.Heading),
            new(UiTexts.Format(UiText.AssignmentClientAndMaster, client, master), AssignmentRowKind.Identity),
            new(UiTexts.Format(UiText.AssignmentPurpose, purpose), AssignmentRowKind.Identity)
        };
        if (state.Pace == Pace.Normal)
        {
            AddCurrentStep(state, lines);
            return lines;
        }
        lines.Add(new(DescribePace(state), AssignmentRowKind.Step));
        if (includeSteps)
        {
            lines.AddRange(StepLines(state.Steps ?? StepList.Empty).Select(line => new AssignmentRow(line, AssignmentRowKind.Step)));
        }
        return lines;
    }

    private static void AddCurrentStep(VisibilityState state, List<AssignmentRow> lines)
    {
        StepView? step = state.Steps?.Now;
        string headline = step == null ? "" : StepHeadline(step);
        if (headline.Length == 0)
        {
            return;
        }

        UiText line = state.Status == VisibilityStatus.Active ? UiText.StepNow : UiText.StepLast;
        lines.Add(new(UiTexts.Format(line, headline), AssignmentRowKind.Step));
    }

    private const int MillisecondsPerSecond = 1000;

    private static string DescribePace(VisibilityState state)
    {
        return state.Pace switch
        {
            Pace.Paused => UiTexts.Get(UiText.AssignmentPaused),
            Pace.SlowMotion => UiTexts.Format(UiText.AssignmentSlowMotion, state.SlowMotionStepMs / (double)MillisecondsPerSecond),
            Pace.Normal => "",
            _ => throw new UnreachableException()
        };
    }

    private static IEnumerable<string> StepLines(StepList steps)
    {
        foreach (StepView before in steps.Before.TakeLast(StepLinesShownBefore))
        {
            yield return UiTexts.Format(UiText.StepBefore, DescribeStep(before));
        }
        if (steps.Now != null)
        {
            yield return UiTexts.Format(UiText.StepNow, DescribeStep(steps.Now));
        }
        UiText nextLine = steps.NextFromAgent ? UiText.StepNextFromAgent : UiText.StepNext;
        foreach (StepView next in steps.Next.Take(StepLinesShownNext))
        {
            yield return UiTexts.Format(nextLine, DescribeStep(next));
        }
    }

    private const int StepLinesShownNext = 3;
    private const int StepLinesShownBefore = 3;

    internal static string DescribeStep(StepView step)
    {
        string headline = StepHeadline(step);
        bool titled = step.Title.Length > 0 || step.Kind == StepKind.Announced;

        return titled && step.Result.Length > 0 ? headline + "\n" + StepText.Clean(step.Result) : headline;
    }

    // The step's title, or its kind and subject, on one line and without its result.
    private static string StepHeadline(StepView step)
    {
        string detail = StepText.Clean(step.Detail);
        if (step.Title.Length > 0 || step.Kind == StepKind.Announced)
        {
            return step.Title.Length > 0 ? StepText.Clean(step.Title) : detail;
        }
        string kind = UiTexts.Get(StepKindText(step.Kind));
        return detail.Length == 0 ? kind : kind + ": " + detail;
    }

    private static UiText StepKindText(StepKind kind)
    {
        return kind switch
        {
            StepKind.Read => UiText.StepRead,
            StepKind.Click => UiText.StepClick,
            StepKind.Type => UiText.StepType,
            StepKind.Input => UiText.StepInput,
            StepKind.Window => UiText.StepWindow,
            StepKind.Launch => UiText.StepLaunch,
            StepKind.Clipboard => UiText.StepClipboard,
            StepKind.Files => UiText.StepFiles,
            StepKind.Command => UiText.StepCommand,
            StepKind.Macro => UiText.StepMacro,
            StepKind.Other or StepKind.Announced => UiText.StepOther,
            _ => throw new UnreachableException()
        };
    }
    protected static string DescribeConsent(ConsentPrompt prompt)
    {
        SessionPresentation details = prompt.Presentation ?? SessionPresentation.Create(null, null);
        return UiTexts.Format(UiText.ConsentPairedMaster, details.ShownMaster) + "\n"
            + UiTexts.Format(UiText.ConsentThisComputer, details.ShownNode) + "\n"
            + UiTexts.Format(UiText.ConsentRequestingAi, details.ShownClient) + "\n"
            + UiTexts.Format(UiText.ConsentPurpose, details.ShownPurpose) + "\n"
            + UiTexts.Format(UiText.ConsentAllowedRights, details.DescribeRights()) + "\n\n" + UiTexts.Get(UiText.ConsentNotice);
    }

    protected static string StatusText(VisibilityState state)
    {
        if (state.AccessLocked)
        {
            return UiTexts.Get(UiText.StatusLocked);
        }
        UiText status = state.Status switch
        {
            VisibilityStatus.Active => UiText.StatusActive,
            VisibilityStatus.Connected => UiText.StatusConnected,
            VisibilityStatus.Idle => UiText.StatusIdle,
            _ => throw new UnreachableException()
        };
        return UiTexts.Get(status);
    }
}

// One line of the assignment banner and what it says.
public sealed record AssignmentRow(string Text, AssignmentRowKind Kind);

// The banner sets its heading apart from the identity lines and the step lines.
public enum AssignmentRowKind
{
    Heading,
    Identity,
    Step
}
