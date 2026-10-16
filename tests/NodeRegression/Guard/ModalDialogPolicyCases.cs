using KLaniBot;
using KLaniBot.Guard;

// The one rule all platforms share. Every combination is listed, so a change to it cannot go unnoticed.
internal static class ModalDialogPolicyCases
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Modal dialog policy: " + name);
            }
            passed++;
        }
        foreach (bool inside in new[] { false, true })
        {
            foreach (bool behind in new[] { false, true })
            {
                foreach (var dialogs in new[] { ModalDialogMode.Pause, ModalDialogMode.AllowInteraction })
                {
                    foreach (bool decision in new[] { false, true })
                    {
                        var expected = behind
                            ? ModalDecision.StopBehindModal
                            : inside
                                && (dialogs == ModalDialogMode.Pause || !decision) ? ModalDecision.StopForModalTarget : ModalDecision.Allow;
                        var actual = ModalDialogPolicy.Decide(inside, behind, dialogs, decision);
                        Check(actual == expected, $"inside={inside} behind={behind} dialogs={dialogs} decision={decision}");
                    }
                }
            }
        }
        string behindMessage = Message(() => ModalDialogPolicy.Require(false, true, ModalDialogMode.AllowInteraction, true));
        Check(behindMessage == ModalDialogPolicy.StopBehindModalMessage, "a target behind a dialog is refused even with dialogs allowed");
        string targetMessage = Message(() => ModalDialogPolicy.Require(true, false, ModalDialogMode.Pause, false));
        Check(targetMessage == ModalDialogPolicy.StopForModalTargetMessage, "a target inside a paused dialog gets the pause message");
        ModalDialogPolicy.Require(true, false, ModalDialogMode.AllowInteraction, true);
        ModalDialogPolicy.Require(false, false, ModalDialogMode.Pause, false);
        return passed;
    }

    private static string Message(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException exception)
        {
            return exception.Message;
        }
        return "";
    }
}
