using System.Diagnostics;
using System.Text.Json.Nodes;
using KLaniBot.Guard;

namespace KLaniBot.Actions;

// What the phases of one execution need to hand to each other. It lives for one request. A macro that
// runs further actions starts a new execution for each of them, so nothing here can be overwritten by
// the steps of a macro that is itself still being executed.
internal sealed class ActionExecution
{
    private readonly Stopwatch _elapsed = new();

    public ActionExecution(ActionRequest request)
    {
        Request = request;
    }

    public ActionRequest Request
    {
        get;
    }
    public AgentDecision? Decision
    {
        get; set;
    }

    // Set when the agent asked to operate a dialog and the administrator leaves that dialog's class to the agent.
    public DialogRouting? DialogRouting
    {
        get; set;
    }

    // What the node read of the target when the request carries a decision. The journal keeps it together with the decision.
    public JsonObject? TargetEvidence
    {
        get; set;
    }

    // Set when the request carries a decision, the node finds no modal dialog for its target, and the target's guard rule does not
    // demand a decision. The decision is recorded and reported as not applied: it is neither routed by dialog class nor handed to
    // the backend as recorded.
    public bool DecisionIgnored
    {
        get; set;
    }

    // Becomes true once the admission of a mutation is in the journal. Recovery reads such an entry as
    // "unknown", so from then on every answer about a failure has to say the same.
    public bool MutationAdmitted
    {
        get; set;
    }

    public bool RecordTrace
    {
        get; set;
    }
    public bool RecordScreenshots
    {
        get; set;
    }

    // A request that was refused before it could reach the desktop gets no "after" image either.
    public bool BeforeImageAttempted
    {
        get; set;
    }
    public JsonObject Screenshots { get; } = new();

    public long ElapsedMilliseconds => _elapsed.ElapsedMilliseconds;

    public void StartTiming()
    {
        _elapsed.Restart();
    }

    // The one place that decides what a failure means for the client. An admitted mutation may already
    // have reached the operating system; anything before that has not touched the machine.
    public ActionOutcome OutcomeAfterFailure()
    {
        if (MutationAdmitted)
        {
            return ActionOutcome.Unknown;
        }
        return ActionOutcome.NotDispatched;
    }
}
