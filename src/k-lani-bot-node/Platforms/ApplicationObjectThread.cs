namespace KLaniBot.Platforms;

// Office automation runs outside the visibility window's synchronous input dispatch. Each call creates, uses and
// releases its COM references on one STA thread; only detached result records return to the caller.
internal static class ApplicationObjectThread
{
    public static ApplicationObjectResult Run(Func<ApplicationObjectResult> operation)
    {
        ApplicationObjectResult result = new(false, "Native application operation did not complete.");
        var thread = new Thread(() => result = Execute(operation))
        {
            IsBackground = true,
            Name = "k-lani-bot Office objects"
        };
        try
        {
            if (OperatingSystem.IsWindows())
            {
                thread.SetApartmentState(ApartmentState.STA);
            }
            thread.Start();
            thread.Join();
        }
        catch (Exception failure)
        {
            return Failure(failure);
        }

        return result;
    }

    private static ApplicationObjectResult Execute(Func<ApplicationObjectResult> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception failure)
        {
            return Failure(failure);
        }
    }

    private static ApplicationObjectResult Failure(Exception failure)
    {
        Console.Error.WriteLine("Application thread: " + KLaniBot.Startup.Diagnostics.Describe(failure));

        return new(false, "Native application operation failed; inspect before another mutation: " + failure.GetType().Name);
    }
}
