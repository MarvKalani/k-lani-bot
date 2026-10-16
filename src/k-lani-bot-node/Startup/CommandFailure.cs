namespace KLaniBot.Startup;

// What a failed command prints on standard error. The type tells an operator where to look; the
// stack trace is only for a developer who asks for it, because service logs are kept and shared.
public static class CommandFailure
{
    public const string DebugVariable = "K_LANI_BOT_DEBUG";

    public static string Describe(Exception exception)
    {
        bool debug = Environment.GetEnvironmentVariable(DebugVariable) == "1";
        return Describe(exception, debug);
    }

    // A developer who asks for the stack trace also gets the message unchanged, paths included.
    public static string Describe(Exception exception, bool includeStackTrace)
    {
        if (!includeStackTrace)
        {
            return Diagnostics.Describe(exception);
        }
        string description = exception.GetType().Name + ": " + exception.Message;
        if (exception.StackTrace != null)
        {
            description += Environment.NewLine + exception.StackTrace;
        }
        return description;
    }
}
