namespace KLaniBot.Guard;

// Allow only says that no modal dialog stands in the way. Scopes, the guard and consent are decided elsewhere.
public enum ModalDecision
{
    // Nothing about a modal dialog stands in the way.
    Allow,

    // The target is inside a modal dialog. The operator's dialog setting and the agent's recorded decision decide.
    StopForModalTarget,

    // Another modal dialog of the same application is open and the target lies behind it.
    StopBehindModal
}

// Use case: an AI agent is working in a legacy program and a modal dialog opens (a certificate warning, a
// file picker, an error box). The operator has chosen whether the agent may operate such dialogs at all
// (the "AI may also operate modal dialogs" setting). Every backend asks this one function, so the rule is the
// same on Windows, macOS and Linux and only the way of finding out the facts differs.
//
// Why a target behind a dialog is refused in both settings: the setting concerns the agent operating the
// dialog, not working around it. A semantic action (UI Automation, AX, AT-SPI) reaches a control even when
// the toolkit blocks the mouse for its window. On Linux this was verified with a GTK dialog: a click on the
// main window behind an open modal dialog still ran. Letting that through would make the dialog optional.
public static class ModalDialogPolicy
{
    public const string StopForModalTargetMessage =
        "Modal dialog: action paused. Review the dialog, or allow dialog operation locally and record the decision in the task.";

    public const string StopBehindModalMessage =
        "Modal dialog: the target lies behind an open modal dialog of the application. Operate or close the dialog first; "
        + "no action was performed.";

    public static ModalDecision Decide(
        bool targetIsInsideModalWindow,
        bool targetIsBehindAnotherModal,
        ModalDialogMode dialogs,
        bool decisionRecorded)
    {
        if (targetIsBehindAnotherModal)
        {
            return ModalDecision.StopBehindModal;
        }
        if (targetIsInsideModalWindow && (dialogs == ModalDialogMode.Pause || !decisionRecorded))
        {
            return ModalDecision.StopForModalTarget;
        }
        return ModalDecision.Allow;
    }

    // Throws InvalidOperationException with StopForModalTargetMessage or StopBehindModalMessage when a dialog stands in the way.
    public static void Require(
        bool targetIsInsideModalWindow,
        bool targetIsBehindAnotherModal,
        ModalDialogMode dialogs,
        bool decisionRecorded)
    {
        switch (Decide(targetIsInsideModalWindow, targetIsBehindAnotherModal, dialogs, decisionRecorded))
        {
            case ModalDecision.StopForModalTarget:
                throw new InvalidOperationException(StopForModalTargetMessage);
            case ModalDecision.StopBehindModal:
                throw new InvalidOperationException(StopBehindModalMessage);
            case ModalDecision.Allow:
                break;
        }
    }
}
