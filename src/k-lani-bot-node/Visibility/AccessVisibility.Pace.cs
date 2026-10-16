using System.Diagnostics;
using KLaniBot.Actions;
using KLaniBot.Guard;

namespace KLaniBot.Visibility;

// The pace gate: every step of an approved session passes here before it reaches the desktop. ESC or the tray menu pause the
// agent or slow it down, and the assignment box lists the steps before, now and next, so the person can follow each one.
public sealed partial class AccessVisibility
{
    private const int StepsKeptBefore = 200;

    private readonly Queue<StepView> _stepsBefore = new();
    private Pace _pace = Pace.Normal;
    private StepView? _stepNow;
    private string _stepOperationId = "";
    private List<StepView> _stepsNext = [];
    private bool _nextFromAgent;
    private int _waitingAtPace;

    public Pace CurrentPace
    {
        get
        {
            lock (_sync)
            {
                return _pace;
            }
        }
    }

    // How long slow motion holds each step, zero at any other pace. A macro plans its time with it.
    public TimeSpan StepDelay
    {
        get
        {
            lock (_sync)
            {
                return _pace == Pace.SlowMotion ? TimeSpan.FromMilliseconds(_options.SlowMotionStepMs) : TimeSpan.Zero;
            }
        }
    }

    // ESC pauses while the person sees the red border and the agent drives the shared keyboard and mouse. In background mode the
    // person keeps typing in their own programs, where ESC is an everyday key.
    public static bool EscapePausesAgent(VisibilityState state)
    {
        return state.Border && state.WindowInteraction == WindowInteractionMode.Foreground;
    }

    public void PauseByEscape()
    {
        ChangePace(Pace.Paused, "paused_by_escape");
    }

    // Slowing down or pausing is always allowed. Speeding up loosens and is refused while the agent's input runs, unless every
    // running request waits at the gate, so the agent cannot click itself free through the tray menu.
    public bool SetPace(Pace pace)
    {
        lock (_sync)
        {
            bool loosening = PaceRank(pace) > PaceRank(_pace);
            if (loosening && _waitingAtPace == 0 && !TryBeginLocalSettingChange(LocalSetting.Pace))
            {
                return false;
            }
        }
        ChangePace(pace, "pace_changed_locally");
        return true;
    }

    // A running macro names the steps it still has, so slow motion can show what comes next.
    public void SetPlannedSteps(IReadOnlyList<StepView> steps)
    {
        lock (_sync)
        {
            _stepsNext = steps.ToList();
            _nextFromAgent = false;
        }
    }

    // Called before a step of a session starts. Normal pace lets it pass; slow motion shows it and lets it pass after the step
    // time; a pause holds it until the person continues or the hold time ends. A step that met a pause is never dispatched
    // afterwards, because the screen may have changed while the person looked at it.
    public PaceDecision AwaitPace(RequestContext caller, StepView step, IReadOnlyList<StepView>? announced, string operationId = "")
    {
        lock (_sync)
        {
            ShowStep(step with
            {
                OperationId = operationId
            }, announced);
            _stepOperationId = operationId;
        }
        Refresh();
        lock (_sync)
        {
            if (_pace == Pace.Normal || !IsApprovedWhileLocked(caller))
            {
                return PaceDecision.Proceed;
            }
            _waitingAtPace++;
            try
            {
                return WaitAtGate(caller);
            }
            finally
            {
                _waitingAtPace--;
            }
        }
    }

    // Caller holds the lock; Monitor.Wait releases it while the person decides.
    private PaceDecision WaitAtGate(RequestContext caller)
    {
        var waited = Stopwatch.StartNew();
        bool metPause = false;
        while (true)
        {
            metPause |= _pace == Pace.Paused;
            bool released = _disposed || !_healthy || !IsApprovedWhileLocked(caller);
            long elapsed = waited.ElapsedMilliseconds;
            if (metPause && (released || _pace != Pace.Paused || elapsed >= _options.PauseHoldMs))
            {
                _stepNow = _stepNow! with
                {
                    Status = StepStatus.Paused
                };
                return PaceDecision.PausedByPerson;
            }
            if (released || _pace == Pace.Normal || elapsed >= _options.SlowMotionStepMs)
            {
                return PaceDecision.Proceed;
            }
            long limit = _pace == Pace.Paused ? _options.PauseHoldMs : _options.SlowMotionStepMs;
            Monitor.Wait(_sync, TimeSpan.FromMilliseconds(Math.Max(1, limit - elapsed)));
        }
    }

    // Nested macro steps replace the outer step, so only the operation still on display may finish it.
    public void FinishStep(string operationId, StepStatus status, string result = "", StepDiagnostics? diagnostics = null)
    {
        lock (_sync)
        {
            if (_stepNow == null || _stepOperationId != operationId || !_healthy || _disposed)
            {
                return;
            }
            _stepNow = _stepNow with
            {
                Status = status,
                Result = StepText.Clean(result),
                Diagnostics = diagnostics
            };
        }

        Refresh();
    }

    private void ChangePace(Pace pace, string journalEvent)
    {
        lock (_sync)
        {
            if (_pace == pace)
            {
                return;
            }
            _pace = pace;
            Record(journalEvent, action: PaceName(pace));
            Monitor.PulseAll(_sync);
        }
        Refresh();
    }

    // Caller holds the lock.
    private void ShowStep(StepView step, IReadOnlyList<StepView>? announced)
    {
        if (_stepNow != null)
        {
            _stepsBefore.Enqueue(_stepNow);
        }
        while (_stepsBefore.Count > StepsKeptBefore)
        {
            _stepsBefore.Dequeue();
        }
        _stepNow = step;
        if (announced != null)
        {
            _stepsNext = announced.ToList();
            _nextFromAgent = true;
        }
        else if (_nextFromAgent && _stepsNext.Count > 0)
        {
            _stepsNext.RemoveAt(0);
        }
    }

    // Caller holds the lock.
    private StepList CurrentSteps()
    {
        if (_sessions.Count == 0)
        {
            return StepList.Empty;
        }
        return new StepList(_stepsBefore.ToArray(), _stepNow, _stepsNext.ToArray(), _nextFromAgent);
    }

    // Caller holds the lock. Clears the list once no session is left, so the next client starts without old steps.
    private void ForgetStepsWithoutSessions()
    {
        if (_sessions.Count != 0)
        {
            return;
        }
        _stepsBefore.Clear();
        _stepNow = null;
        _stepOperationId = "";
        _stepsNext = [];
        _nextFromAgent = false;
    }

    private bool IsApprovedWhileLocked(RequestContext caller)
    {
        bool approved = _sessions.TryGetValue(Key(caller), out Session? session) && session.Consent == SessionConsent.Allowed;
        return _healthy && !_options.AccessLocked && approved;
    }

    private static int PaceRank(Pace pace)
    {
        return pace switch
        {
            Pace.Paused => 0,
            Pace.SlowMotion => 1,
            Pace.Normal => 2,
            _ => throw new UnreachableException()
        };
    }

    private static string PaceName(Pace pace)
    {
        return pace switch
        {
            Pace.Normal => "normal",
            Pace.Paused => "paused",
            Pace.SlowMotion => "slow_motion",
            _ => throw new UnreachableException()
        };
    }
}
