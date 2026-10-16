using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Common;
using KLaniBot.Guard;
using KLaniBot.Startup;

namespace KLaniBot.Visibility;

// The settings the person at the machine can change locally. They live outside the agent workspace
// (visibility-preferences.json) so that neither the AI nor a remote client can edit them. Instances
// are immutable: a change is a new value made with `with`, so a setting that one code path forgets to
// copy cannot be reset silently.
public sealed record VisibilityOptions
{
    public bool ScreenBorder { get; init; } = true;
    public bool RecordActionScreenshots
    {
        get; init;
    }
    public WindowInteractionMode WindowInteraction { get; init; } = WindowInteractionMode.Foreground;
    public ModalDialogMode ModalDialogs { get; init; } = ModalDialogMode.Pause;
    public ConsentMode ConsentMode { get; init; } = ConsentMode.Ask;
    public int ActivityHoldMs { get; init; } = 750;

    // How long slow motion shows each step before it runs, and how long a pause holds a step before the agent is told. The hold
    // stays below the actuator's 60 s action deadline, so a held request is answered instead of ending the actuator.
    public int SlowMotionStepMs { get; init; } = 3000;
    public int PauseHoldMs { get; init; } = 45000;

    // Set by "Alle Zugriffe sperren". It is saved like the other settings, so a lock survives a restart, and it
    // outranks unattended mode until the person lifts it in the same menu.
    public bool AccessLocked
    {
        get; init;
    }

    // Throws InvalidDataException for an undefined window, dialog or consent setting, a hold time outside 250 to 2000 ms or a pace
    // setting outside its range.
    public void Validate()
    {
        if (!Enum.IsDefined(WindowInteraction) || !Enum.IsDefined(ModalDialogs))
        {
            throw new InvalidDataException("Invalid local window interaction settings.");
        }
        if (!Enum.IsDefined(ConsentMode) || ActivityHoldMs is < 250 or > 2000)
        {
            throw new InvalidDataException("Invalid visibility settings. Consent mode is ask or unattended; activity hold is 250–2000 ms.");
        }
        if (SlowMotionStepMs is < 500 or > 30000 || PauseHoldMs is < 1000 or > 50000)
        {
            throw new InvalidDataException("Invalid pace settings. A slow-motion step is 500–30000 ms; a pause holds 1000–50000 ms.");
        }
    }
}

// The question shown to the person for one session; the answer must carry its challenge.
public sealed record ConsentPrompt(string Key, string Principal, string Challenge, SessionPresentation? Presentation = null);

// Everything an indicator shows: status, sessions, border, open question, settings and who is working.
public sealed record VisibilityState(
    VisibilityStatus Status,
    int Connections,
    bool Border,
    ConsentPrompt? Prompt,
    bool Unattended,
    bool ScreenBorder,
    SessionPresentation? Presentation = null,
    WindowInteractionMode WindowInteraction = WindowInteractionMode.Foreground,
    ModalDialogMode ModalDialogs = ModalDialogMode.Pause,
    bool RecordActionScreenshots = false,
    bool AccessLocked = false,
    Pace Pace = Pace.Normal,
    StepList? Steps = null,
    int SlowMotionStepMs = 3000);

// The native tray icon, border and consent dialog of the operated node. Only a node implements it; a controller
// never shows desktop indicators for a machine it drives remotely. Render must throw when it cannot show the
// state: a display that fails quietly would let actions run while the person sees nothing, and AccessVisibility
// stops all actions after the first failure.
public interface IVisibilityDisplay : IDisposable
{
    void Render(VisibilityState state);
}

// Whether an action may start. A set Rejection means nothing may run and the caller reports it; otherwise the action
// runs, and a set Activity is disposed when it has finished.
public sealed record ActivityStart(IDisposable? Activity, JsonObject? Rejection)
{
    public static readonly ActivityStart WithoutActivity = new(null, null);

    public static ActivityStart Started(IDisposable activity) => new(activity, null);

    public static ActivityStart Rejected(JsonObject rejection) => new(null, rejection);
}

// Marks a failure of the indicator itself, as opposed to a refused session or a failed journal, so that
// an operator can tell "the screen could not show it" from "the node would not allow it".
public sealed class IndicatorRenderException(Exception cause)
    : Exception("Visible activity indicator could not be drawn.", cause);

// Use case: an AI client wants to drive a desktop that a person may be sitting at. This type is the
// node's local conscience: it shows that an AI is connected or working, asks the person once per
// session whether that is acceptable, and refuses to let any action start while the indicator cannot
// be shown.
//
// Trust boundary: every decision here comes from the person at the machine (tray menu, dialog) or
// from the operator's configuration, never from the AI. The AI can inject mouse and keyboard input
// into the same desktop, so while an action runs, the tray cannot grant consent or loosen a setting;
// such attempts are refused and written to the audit journal.
//
// Why consent is per session and not per action: delegated operation has nobody who could answer a
// question for every dialog. A changed assignment (SessionPresentation) starts a new question, so
// an approval cannot be reused for a different purpose.
public sealed partial class AccessVisibility : IDisposable
{
    private const int MaximumSessions = 64;
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromSeconds(75);
    private static readonly TimeSpan DeferredReminderInterval = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions PreferenceFormat = new() { PropertyNameCaseInsensitive = true };

    // Actions that read or move things on the screen get the visible border, because the person has to
    // be able to see that the AI is working right now.
    private static readonly HashSet<Tool> BorderActions = [Tool.RunMacro, Tool.RunTerminalCommand, Tool.LaunchProcess];

    // A setting the person at the machine changes in the indicator; each change is journaled.
    private enum LocalSetting
    {
        ConsentApproval,
        UnattendedMode,
        ScreenBorder,
        WindowInteraction,
        ScreenshotRecording,
        AccessLock,
        Pace
    }

    // One controller session as the gate tracks it: who, the consent state and when it was last seen.
    private sealed class Session
    {
        public required string Principal
        {
            get; init;
        }
        public long Seen
        {
            get; set;
        }
        public SessionConsent Consent { get; set; } = SessionConsent.New;
        public long DeferredAt
        {
            get; set;
        }
        public string Challenge { get; private set; } = NewChallenge();
        public SessionPresentation? Presentation
        {
            get; set;
        }

        // A fresh challenge makes an older prompt on screen worthless: a decision that quotes the
        // previous challenge is rejected, so a stale or replayed answer cannot approve the new state.
        public void ResetTo(SessionConsent consent)
        {
            Consent = consent;
            Challenge = NewChallenge();
        }
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly HashSet<LocalSetting> _rejectionsRecorded = new();
    private readonly IVisibilityDisplay _display;
    private readonly TimeProvider _clock;
    private readonly Action<JsonObject> _audit;
    private readonly Action<VisibilityOptions>? _save;
    private VisibilityOptions _options;
    private string? _activitySessionKey;

    // Number of actions running right now, and how many of those need the border.
    private int _active;
    private int _sensitive;

    // The indicator stays up for ActivityHoldMs after the last action, otherwise a click that takes a
    // few milliseconds would never be visible to the person.
    private bool _held;
    private long _lastActivity;
    private bool _healthy = true;
    private bool _disposed;

    public AccessVisibility(
        VisibilityOptions options,
        IVisibilityDisplay display,
        Action<JsonObject> audit,
        TimeProvider? clock = null,
        Action<VisibilityOptions>? save = null)
    {
        options.Validate();
        _options = options;
        _display = display;
        _audit = audit;
        _clock = clock ?? TimeProvider.System;
        _save = save;
        Refresh();
    }

    private static string NewChallenge()
    {
        return Guid.CreateVersion7().ToString();
    }

    private static string Key(RequestContext caller)
    {
        return caller.PrincipalId + ":" + caller.SessionId;
    }

    // Local settings and approvals are blocked for the whole time an action runs or is still held.
    private bool ActionInProgress => _active != 0 || _held;

    // Rethrows the Exception of a failed journal write after marking the indicator unhealthy.
    private void Record(string eventName, string? sessionKey = null, string? action = null)
    {
        lock (_sync)
        {
            try
            {
                _audit(new JsonObject
                {
                    ["event"] = eventName,
                    ["session_key"] = sessionKey,
                    ["action"] = action,
                    ["request_id"] = Guid.CreateVersion7().ToString(),
                    ["timestamp"] = _clock.GetUtcNow().ToString("O")
                });
            }
            catch
            {
                // Without a writable journal nobody can prove what the AI did, so nothing may run.
                _healthy = false;
                throw;
            }
        }
    }

    public void SetSessionPresentation(RequestContext caller, SessionPresentation presentation)
    {
        Touch(caller);
        lock (_sync)
        {
            Session session = _sessions[Key(caller)];
            if (session.Presentation != null && session.Presentation != presentation)
            {
                // A changed assignment needs a new local decision, even on the same transport session.
                session.ResetTo(SessionConsent.New);
            }
            session.Presentation = presentation;
        }
        Refresh();
    }

    public void Touch(RequestContext caller, bool connected = true)
    {
        lock (_sync)
        {
            string key = Key(caller);
            if (!connected)
            {
                Disconnect(key);
            }
            else if (_sessions.TryGetValue(key, out Session? session))
            {
                session.Seen = _clock.GetTimestamp();
            }
            else
            {
                Connect(key, caller);
            }
        }
        Refresh();
    }

    private void Disconnect(string key)
    {
        if (_sessions.Remove(key))
        {
            Record("session_disconnected", key);
        }
    }

    // Throws InvalidOperationException when the session limit is reached.
    private void Connect(string key, RequestContext caller)
    {
        if (_sessions.Count >= MaximumSessions)
        {
            throw new InvalidOperationException("Visible session limit reached.");
        }
        _sessions[key] = new Session
        {
            Principal = caller.PrincipalId,
            Seen = _clock.GetTimestamp()
        };
        Record("session_connected", key);
    }

    // Reading the state has no side effect. Sessions expire and deferred questions return in Refresh,
    // which every request and the tray's periodic pump call.
    public VisibilityState State
    {
        get
        {
            lock (_sync)
            {
                return Snapshot();
            }
        }
    }

    // Removes sessions whose client went away without ending them, and asks again about a question the
    // person postponed. Caller holds the lock.
    private void ExpireSessions()
    {
        long now = _clock.GetTimestamp();
        foreach (KeyValuePair<string, Session> pair in _sessions.ToArray())
        {
            Session session = pair.Value;
            if (_active == 0 && _clock.GetElapsedTime(session.Seen, now) >= SessionTimeout)
            {
                _sessions.Remove(pair.Key);
                Record("session_expired", pair.Key);
                continue;
            }
            if (session.Consent == SessionConsent.Deferred && _clock.GetElapsedTime(session.DeferredAt, now) >= DeferredReminderInterval)
            {
                session.ResetTo(SessionConsent.Pending);
                Record("consent_reprompted", pair.Key);
            }
        }
    }

    // Ends the "AI is working" display once the hold time after the last action has passed. Caller holds the lock.
    private void ReleaseActivityHold()
    {
        if (!_held || _active != 0)
        {
            return;
        }
        TimeSpan sinceLastActivity = _clock.GetElapsedTime(_lastActivity, _clock.GetTimestamp());
        if (sinceLastActivity >= TimeSpan.FromMilliseconds(_options.ActivityHoldMs))
        {
            _held = false;
            _sensitive = 0;
            _rejectionsRecorded.Clear();
        }
    }

    private VisibilityStatus CurrentStatus()
    {
        if (ActionInProgress)
        {
            return VisibilityStatus.Active;
        }
        return _sessions.Count > 0 ? VisibilityStatus.Connected : VisibilityStatus.Idle;
    }

    private Session? FindActivitySession()
    {
        if (_activitySessionKey == null)
        {
            return null;
        }
        _sessions.TryGetValue(_activitySessionKey, out Session? session);
        return session;
    }

    private SessionPresentation? ChoosePresentation(Session? activitySession, Session? allowed, Session? pending)
    {
        // A pending second client must not replace the identity of the client
        // whose action is currently running on this desktop.
        SessionPresentation? runningClient = ActionInProgress ? activitySession?.Presentation : null;
        SessionPresentation? anyClient = _sessions.Values.FirstOrDefault(session => session.Presentation != null)?.Presentation;
        return runningClient ?? allowed?.Presentation ?? pending?.Presentation ?? anyClient;
    }

    private VisibilityState Snapshot()
    {
        KeyValuePair<string, Session> pendingPair = _sessions.FirstOrDefault(pair => pair.Value.Consent == SessionConsent.Pending);
        Session? pending = pendingPair.Value;
        ConsentPrompt? prompt = pending == null
            ? null
            : new ConsentPrompt(pendingPair.Key, pending.Principal, pending.Challenge, pending.Presentation);
        Session? activitySession = FindActivitySession();
        Session? allowed = activitySession?.Consent == SessionConsent.Allowed
            ? activitySession
            : _sessions.Values.FirstOrDefault(session => session.Consent == SessionConsent.Allowed);
        bool borderVisible = _options.ScreenBorder && (_sensitive > 0 || allowed != null);
        return new VisibilityState(
            CurrentStatus(),
            _sessions.Count,
            borderVisible,
            prompt,
            _options.ConsentMode == ConsentMode.Unattended,
            _options.ScreenBorder,
            ChoosePresentation(activitySession, allowed, pending),
            _options.WindowInteraction,
            _options.ModalDialogs,
            _options.RecordActionScreenshots,
            _options.AccessLocked,
            _pace,
            CurrentSteps(),
            _options.SlowMotionStepMs);
    }

    // Throws InvalidOperationException after an earlier host failure and IndicatorRenderException when the display cannot show the state.
    public void Refresh()
    {
        VisibilityState state;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            if (!_healthy)
            {
                throw new InvalidOperationException("Visible activity host failed; restart required.");
            }
            ExpireSessions();
            ForgetStepsWithoutSessions();
            ReleaseActivityHold();
            state = Snapshot();
        }
        try
        {
            _display.Render(state);
        }
        catch (Exception exception)
        {
            // Renderers are platform code (GTK, Win32, AppKit) with open-ended failure types. An indicator
            // that cannot be drawn is the same as no indicator: further actions are refused.
            lock (_sync)
            {
                _healthy = false;
                Monitor.PulseAll(_sync);
            }
            throw new IndicatorRenderException(exception);
        }
    }

    // Throws ArgumentException for New or Pending, which are no decisions.
    private static string ConsentEventName(SessionConsent decision)
    {
        return decision switch
        {
            SessionConsent.Allowed => "consent_allowed",
            SessionConsent.Denied => "consent_denied",
            SessionConsent.Deferred => "consent_deferred",
            SessionConsent.New or SessionConsent.Pending => throw new ArgumentException("Invalid local consent decision."),
            _ => throw new UnreachableException()
        };
    }

    private static string AuditName(LocalSetting setting)
    {
        return setting switch
        {
            LocalSetting.ConsentApproval => "consent_approval",
            LocalSetting.UnattendedMode => "unattended_mode",
            LocalSetting.ScreenBorder => "screen_border",
            LocalSetting.WindowInteraction => "window_interaction",
            LocalSetting.ScreenshotRecording => "screenshot_recording",
            LocalSetting.AccessLock => "access_lock",
            LocalSetting.Pace => "pace",
            _ => throw new UnreachableException()
        };
    }

    // Returns false when the answer was not accepted: unknown session, stale challenge, or an attempt
    // to approve while an action is running. The last case is what our own injected input looks like.
    public bool Decide(string key, string challenge, SessionConsent decision)
    {
        string eventName = ConsentEventName(decision);
        lock (_sync)
        {
            if (!_sessions.TryGetValue(key, out Session? session) || session.Challenge != challenge)
            {
                return false;
            }
            if (decision == SessionConsent.Allowed && _options.AccessLocked)
            {
                return false;
            }
            if (decision == SessionConsent.Allowed && ActionInProgress)
            {
                RecordRejectedDuringAction(LocalSetting.ConsentApproval);
                return false;
            }
            session.Consent = decision;
            session.DeferredAt = _clock.GetTimestamp();
            Record(eventName, key);
        }
        Refresh();
        return true;
    }

    // Refusing to deny or defer would not help an attacker, so only the loosening directions are blocked.
    private bool TryBeginLocalSettingChange(LocalSetting setting)
    {
        if (!ActionInProgress)
        {
            return true;
        }
        RecordRejectedDuringAction(setting);
        return false;
    }

    // One entry per setting and activity period is enough to expose injected input. More would let the
    // AI fill the journal just by clicking the same menu item in a loop.
    private void RecordRejectedDuringAction(LocalSetting setting)
    {
        if (_rejectionsRecorded.Add(setting))
        {
            Record("local_setting_rejected_during_action", action: AuditName(setting));
        }
    }

    // The preference is written before it takes effect, so a failing save leaves memory and disk on the same value.
    private void CommitOptions(VisibilityOptions updated)
    {
        _save?.Invoke(updated);
        _options = updated;
    }

    public bool SetUnattended(bool enabled)
    {
        lock (_sync)
        {
            if (!TryBeginLocalSettingChange(LocalSetting.UnattendedMode))
            {
                return false;
            }
            CommitOptions(_options with
            {
                ConsentMode = enabled ? ConsentMode.Unattended : ConsentMode.Ask
            });
            // Leaving unattended mode withdraws every approval it gave, so each client is asked again. While access is
            // locked, the sessions stay refused either way.
            SessionConsent consent = enabled ? SessionConsent.Allowed : SessionConsent.New;
            foreach (Session session in _sessions.Values)
            {
                session.ResetTo(_options.AccessLocked ? SessionConsent.Denied : consent);
            }
            Record(enabled ? "unattended_enabled_locally" : "unattended_disabled_locally");
        }
        Refresh();
        return true;
    }

    public bool SetBorder(bool enabled)
    {
        lock (_sync)
        {
            if (!TryBeginLocalSettingChange(LocalSetting.ScreenBorder))
            {
                return false;
            }
            CommitOptions(_options with
            {
                ScreenBorder = enabled
            });
            Record(enabled ? "border_enabled_locally" : "border_disabled_locally");
        }
        Refresh();
        return true;
    }

    // False when nothing changed: an action is running, or a value is not a member of its enum.
    public bool SetWindowInteraction(WindowInteractionMode mode, ModalDialogMode dialogs)
    {
        if (!Enum.IsDefined(mode) || !Enum.IsDefined(dialogs))
        {
            return false;
        }
        lock (_sync)
        {
            if (!TryBeginLocalSettingChange(LocalSetting.WindowInteraction))
            {
                return false;
            }
            CommitOptions(_options with
            {
                WindowInteraction = mode,
                ModalDialogs = dialogs
            });
            Record("window_interaction_changed_locally", action: mode + ":" + dialogs);
        }
        Refresh();
        return true;
    }

    public bool SetScreenshotRecording(bool enabled)
    {
        lock (_sync)
        {
            if (!TryBeginLocalSettingChange(LocalSetting.ScreenshotRecording))
            {
                return false;
            }
            CommitOptions(_options with
            {
                RecordActionScreenshots = enabled
            });
            Record(enabled ? "screenshot_recording_enabled_locally" : "screenshot_recording_disabled_locally");
        }
        Refresh();
        return true;
    }

    // Locking is always allowed, even during an action, because it only ever reduces access. Unlike RevokeAll it
    // also holds for clients that connect later and is saved, so neither unattended mode nor a restart reopens it.
    public void LockAccess()
    {
        lock (_sync)
        {
            CommitOptions(_options with
            {
                AccessLocked = true
            });
            foreach (Session session in _sessions.Values)
            {
                session.ResetTo(SessionConsent.Denied);
            }
            Record("access_locked_locally");
            Monitor.PulseAll(_sync);
        }
        Refresh();
    }

    // Lifting the lock loosens access, so it is refused while an action runs, like every other loosening.
    // Every client is asked again, or admitted again if unattended mode is on.
    public bool UnlockAccess()
    {
        lock (_sync)
        {
            if (!TryBeginLocalSettingChange(LocalSetting.AccessLock))
            {
                return false;
            }
            CommitOptions(_options with
            {
                AccessLocked = false
            });
            foreach (Session session in _sessions.Values)
            {
                session.ResetTo(SessionConsent.New);
            }
            Record("access_unlocked_locally");
        }
        Refresh();
        return true;
    }

    // Revoking is always allowed, even during an action, because it only ever reduces access. It ends the sessions
    // that exist now; quitting the program uses it, and a later client is asked again.
    public void RevokeAll()
    {
        lock (_sync)
        {
            foreach (Session session in _sessions.Values)
            {
                session.ResetTo(SessionConsent.Denied);
            }
            Record("all_sessions_revoked_locally");
            Monitor.PulseAll(_sync);
        }
        Refresh();
    }

    public void RequestApprovalLocally()
    {
        lock (_sync)
        {
            if (_options.AccessLocked || ActionInProgress || _sessions.Values.Any(session => session.Consent == SessionConsent.Pending))
            {
                return;
            }
            KeyValuePair<string, Session> first = _sessions.FirstOrDefault();
            if (first.Value == null)
            {
                return;
            }
            first.Value.ResetTo(SessionConsent.Pending);
            Record("consent_reopened_locally", first.Key);
        }
        Refresh();
    }

    // Discovery calls are exempt so that a client can learn what the node offers and that consent is
    // missing, before anything has touched the machine.
    public static bool NeedsConsent(Tool action)
    {
        return action is not (Tool.GetMachineCapabilities or Tool.ListAvailableMcps or Tool.GetActionStatus or Tool.EndSession);
    }

    // Asked again directly before dispatch, because the person can revoke while the advisor is still thinking.
    // Throws UnauthorizedAccessException when the session is not approved or access is locked.
    public void RequireApproval(RequestContext caller)
    {
        if (!IsApproved(caller))
        {
            throw new UnauthorizedAccessException("Local consent is absent or revoked. No additional step dispatched.");
        }
    }

    // Asked while the node waits for the result of an action, so a person who stops the session ends the wait at once.
    public bool IsApproved(RequestContext caller)
    {
        lock (_sync)
        {
            return IsApprovedWhileLocked(caller);
        }
    }

    public static bool NeedsBorder(Tool action)
    {
        if (BorderActions.Contains(action))
        {
            return true;
        }
        return ToolFeatures.ShowsBorder(ToolFeatures.Of(ToolContract.Get(action)));
    }

    // Opens the activity window for one action.
    public ActivityStart Begin(Tool action, RequestContext caller)
    {
        if (action == Tool.EndSession)
        {
            Touch(caller, false);
            return ActivityStart.WithoutActivity;
        }
        JsonObject? connectionRejection = ConnectOrReject(caller);
        if (connectionRejection != null)
        {
            return ActivityStart.Rejected(connectionRejection);
        }
        bool needsConsent = NeedsConsent(action);
        string sessionKey = Key(caller);
        JsonObject? rejection = null;
        lock (_sync)
        {
            if (!_healthy)
            {
                return ActivityStart.Rejected(IndicatorUnavailable("Visible activity indicator unavailable."));
            }
            if (needsConsent)
            {
                rejection = RejectOrStartActivity(sessionKey, action);
            }
        }
        return StartAfterIndicator(action, sessionKey, needsConsent, rejection);
    }

    // The indicator is shown outside the lock; a failed indicator outranks the consent decision.
    private ActivityStart StartAfterIndicator(Tool action, string sessionKey, bool needsConsent, JsonObject? rejection)
    {
        JsonObject? indicatorRejection = ShowIndicatorOrReject(rejection == null && needsConsent);
        if (indicatorRejection != null)
        {
            return ActivityStart.Rejected(indicatorRejection);
        }
        if (rejection != null)
        {
            return ActivityStart.Rejected(rejection);
        }
        if (!needsConsent)
        {
            return ActivityStart.WithoutActivity;
        }

        Record("action_indicator_visible", sessionKey, ToolNames.Of(action));
        return ActivityStart.Started(new Activity(this, action, sessionKey));
    }

    private JsonObject? ConnectOrReject(RequestContext caller)
    {
        try
        {
            Touch(caller);
            return null;
        }
        catch (IndicatorRenderException exception)
        {
            return RenderRejection(exception);
        }
        catch (Exception exception)
        {
            // Session limit, failed journal or a host already marked unhealthy: whatever it was, the
            // action must not run unseen. The type is reported so the cause is not guessed at.
            return SessionRejection(exception);
        }
    }

    // If the display fails after an action was started, that action is ended again so that the
    // failed indicator cannot be used to run it anyway.
    private JsonObject? ShowIndicatorOrReject(bool activityWasStarted)
    {
        try
        {
            Refresh();
            return null;
        }
        catch (Exception exception)
        {
            // Same reason as in ConnectOrReject: nothing may run once the indicator is in doubt.
            if (activityWasStarted)
            {
                EndActivity();
            }
            if (exception is IndicatorRenderException renderFailure)
            {
                return RenderRejection(renderFailure);
            }
            return SessionRejection(exception);
        }
    }

    private static JsonObject RenderRejection(IndicatorRenderException exception)
    {
        Exception cause = exception.InnerException ?? exception;
        Console.Error.WriteLine("Activity indicator could not be drawn: " + Diagnostics.Describe(cause));
        return IndicatorUnavailable("Indicator could not be displayed before dispatch (" + cause.GetType().Name + ").");
    }

    private static JsonObject SessionRejection(Exception exception)
    {
        Console.Error.WriteLine("Visible session unavailable: " + Diagnostics.Describe(exception));
        return IndicatorUnavailable("Visible activity indicator unavailable (" + exception.GetType().Name + ").");
    }

    private JsonObject? RejectOrStartActivity(string sessionKey, Tool action)
    {
        if (_options.AccessLocked)
        {
            return ActionResults.Rejected(
                ActionErrorCode.ConsentDenied,
                "Access is locked on this machine. The person there can lift the lock in the k-lani-bot menu. No OS action dispatched.");
        }
        Session session = _sessions[sessionKey];
        if (session.Consent == SessionConsent.New)
        {
            RequestConsent(session, sessionKey);
        }
        if (session.Consent != SessionConsent.Allowed)
        {
            return RejectForConsent(session);
        }
        StartActivity(sessionKey, action);
        return null;
    }

    private void RequestConsent(Session session, string sessionKey)
    {
        bool allowedByOperator = _options.ConsentMode == ConsentMode.Unattended;
        session.Consent = allowedByOperator ? SessionConsent.Allowed : SessionConsent.Pending;
        Record(allowedByOperator ? "consent_allowed_by_operator_config" : "consent_requested", sessionKey);
    }

    private JsonObject RejectForConsent(Session session)
    {
        ActionErrorCode code = session.Consent switch
        {
            SessionConsent.Deferred => ActionErrorCode.ConsentDeferred,
            SessionConsent.Denied => ActionErrorCode.ConsentDenied,
            SessionConsent.New or SessionConsent.Pending or SessionConsent.Allowed => ActionErrorCode.ConsentRequired,
            _ => throw new UnreachableException()
        };
        JsonObject rejection = ActionResults.Rejected(
            code,
            "Approve this session locally from the k-lani-bot tray/menu. No OS action dispatched.");
        if (session.Consent == SessionConsent.Deferred)
        {
            int waitedSeconds = (int)_clock.GetElapsedTime(session.DeferredAt).TotalSeconds;
            rejection["retry_after_seconds"] = Math.Max(1, (int)DeferredReminderInterval.TotalSeconds - waitedSeconds);
        }
        return rejection;
    }

    private void StartActivity(string sessionKey, Tool action)
    {
        _activitySessionKey = sessionKey;
        _active++;
        if (NeedsBorder(action))
        {
            _sensitive++;
        }
        _held = true;
        _lastActivity = _clock.GetTimestamp();
        Record("action_indicator_requested", sessionKey, ToolNames.Of(action));
    }

    // The audit entry is written only for an action that really ran; the failure path above ends it silently.
    private void EndActivity(Tool? action = null, string? sessionKey = null)
    {
        lock (_sync)
        {
            if (_active > 0)
            {
                _active--;
            }
            _lastActivity = _clock.GetTimestamp();
            if (action != null)
            {
                Record("action_indicator_completed", sessionKey, ToolNames.Of(action.Value));
            }
        }
    }

    // An action that is running; disposing it ends the activity on the indicator.
    private sealed class Activity(AccessVisibility owner, Tool action, string key) : IDisposable
    {
        private AccessVisibility? _owner = owner;

        public void Dispose()
        {
            AccessVisibility? ownerToNotify = Interlocked.Exchange(ref _owner, null);
            ownerToNotify?.EndActivity(action, key);
        }
    }

    private static JsonObject IndicatorUnavailable(string error)
    {
        return ActionResults.Rejected(ActionErrorCode.IndicatorUnavailable, error);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _healthy = false;
            _sessions.Clear();
            Monitor.PulseAll(_sync);
        }
    }

    private static string PreferencesFile(NodePolicy policy)
    {
        return Path.Combine(policy.StateRoot, "visibility-preferences.json");
    }

    // A link in the path could redirect the preferences to a location that the AI controls.
    // Throws InvalidDataException when a part of the path is a link.
    private static void RejectLinks(string path)
    {
        for (string? part = path; part != null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Visibility preference links are forbidden.");
                }
            }
            catch (FileNotFoundException)
            {
                // A part that does not exist yet cannot be a link.
            }
            catch (DirectoryNotFoundException)
            {
                // Same for directories that are created on the first save.
            }
        }
    }

    public static VisibilityOptions LoadOptions(NodePolicy policy)
    {
        string file = PreferencesFile(policy);
        if (!File.Exists(file))
        {
            policy.Visibility.Validate();
            return policy.Visibility;
        }
        RejectLinks(file);
        VisibilityOptions options = ParseOptions(File.ReadAllText(file));
        options.Validate();
        return options;
    }

    // An unknown consent mode is an error and not a default: a typo must not quietly turn a node that
    // asks into one that does not.
    // Throws InvalidDataException when the preferences are not valid.
    internal static VisibilityOptions ParseOptions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<VisibilityOptions>(json, PreferenceFormat)
                ?? throw new InvalidDataException("Invalid local visibility preferences.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Invalid local visibility preferences.", exception);
        }
    }

    public static void SaveOptions(NodePolicy policy, VisibilityOptions options)
    {
        string file = PreferencesFile(policy);
        string temporary = file + "." + Guid.CreateVersion7();
        RejectLinks(file);
        try
        {
            using (var stream = PrivateFile.OpenWrite(temporary, FileMode.CreateNew))
            {
                JsonSerializer.Serialize(stream, options);
                stream.Flush(true);
            }
            File.Move(temporary, file, true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
