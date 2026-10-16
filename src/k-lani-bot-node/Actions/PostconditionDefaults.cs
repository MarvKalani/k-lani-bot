namespace KLaniBot.Actions;

// Use case: the dispatcher waits for a postcondition, and a macro plans its time with the same wait, so both read it here.
public static class PostconditionDefaults
{
    // A met postcondition returns at once, so a long default only costs time when the expected result never appears,
    // and it gives an application that loads its data enough time before the agent acts again.
    public const int WaitMilliseconds = 30000;
}
