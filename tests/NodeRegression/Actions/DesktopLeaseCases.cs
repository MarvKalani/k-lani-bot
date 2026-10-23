using KLaniBot.Actions;

// A second MCP connection used to get only "Desktop leased to another session", with no hint when the desktop frees up
// or why. An agent then opened yet another connection per call and kept locking itself out.
internal static class DesktopLeaseCases
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Desktop lease: " + name);
            }
            passed++;
        }
        RefusalSaysWhenAndHowWithoutNamingTheHolder(Check);
        AnExpiredLeasePassesToTheNextConnection(Check);
        return passed;
    }

    private static void RefusalSaysWhenAndHowWithoutNamingTheHolder(Action<bool, string> check)
    {
        var desktop = new DesktopObservationState();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        desktop.TakeLease("operator:holder-session", start);
        string message = "";
        try
        {
            desktop.TakeLease("operator:other-session", start.AddSeconds(100));
        }
        catch (UnauthorizedAccessException exception)
        {
            message = exception.Message;
        }
        check(message.Contains("for 200 more seconds", StringComparison.Ordinal), "the refusal names the remaining time");
        check(message.Contains("keep one connection for a whole task", StringComparison.Ordinal), "the refusal says how to avoid it");
        check(message.Contains("end_session", StringComparison.Ordinal), "the refusal names the release call");
        check(!message.Contains("holder-session", StringComparison.Ordinal), "the refusal does not reveal the holder's session");
    }

    private static void AnExpiredLeasePassesToTheNextConnection(Action<bool, string> check)
    {
        var desktop = new DesktopObservationState();
        DateTimeOffset start = DateTimeOffset.UtcNow;
        desktop.TakeLease("operator:holder-session", start);
        desktop.TakeLease("operator:other-session", start.AddMinutes(5).AddSeconds(1));
        check(desktop.Snapshot.Length == 0, "the next connection starts without the previous connection's snapshot");
    }
}
