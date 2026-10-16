using System.Diagnostics;

namespace KLaniBot.Platforms;

// Use case: the Linux and macOS backends start an application the operator has allowed in the node
// policy. The agent only controls the arguments, so they are passed as a list and never joined
// into a string or handed to a shell; a path with spaces or a backslash reaches the program exactly
// as sent.
//
// Standard input is closed right away. In the stdio worker the inherited standard input is the
// private control channel from the supervisor, and a launched program must never read from it.
public static class ProcessLauncher
{
    public static ProcessStartInfo CreateStartInfo(string executablePath, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            RedirectStandardInput = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return start;
    }

    public static Process? StartDetached(ProcessStartInfo start)
    {
        var process = Process.Start(start);
        process?.StandardInput.Close();
        return process;
    }
}
