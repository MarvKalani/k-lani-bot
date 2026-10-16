using System.Diagnostics;
using System.Text;

namespace KLaniBot.Common;

// Use case: the node calls small helper programs (xdotool, wmctrl, scrot, docker, adb, open) and, for
// the shell permission, runs commands an agent wrote. All of them need the same guarantees: a hard
// deadline, bounded output, and no way to reach the node's own channels.
//
// Standard input is closed instead of inherited. In the stdio worker the inherited standard input is
// the private control channel from the supervisor; a child such as "cat" would otherwise wait on it
// until the deadline, and a child that outlives the call could read the supervisor's frames.
// ExitCode is -1 when the process could not be started or missed its deadline; Error then says which.
public sealed record ProcessResult(bool Success, string Output, string Error, int ExitCode);

// Shell tools, installers and platform helpers start programs here: with an argument list instead of a command line, no inherited standard
// input, bounded output and a deadline.
public static class ProcessRunner
{
    private const int MaximumCapturedCharacters = 1048576;

    public static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments, string? workingDirectory)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        return start;
    }

    public static ProcessResult Run(
        string executable,
        IEnumerable<string> arguments,
        int timeout = 3000,
        string? workingDirectory = null)
    {
        using var process = new Process { StartInfo = CreateStartInfo(executable, arguments, workingDirectory) };
        bool started = false;
        using var deadline = new CancellationTokenSource(Math.Clamp(timeout, 1, 3600000));
        try
        {
            process.Start();
            started = true;
            process.StandardInput.Close();
            var standardOutput = ReadBounded(process.StandardOutput, deadline.Token);
            var standardError = ReadBounded(process.StandardError, deadline.Token);
            process.WaitForExitAsync(deadline.Token).GetAwaiter().GetResult();
            Task.WhenAll(standardOutput, standardError).WaitAsync(deadline.Token).GetAwaiter().GetResult();
            return new ProcessResult(
                process.ExitCode == 0,
                standardOutput.GetAwaiter().GetResult(),
                standardError.GetAwaiter().GetResult(),
                process.ExitCode);
        }
        catch (Exception exception)
        {
            if (started)
            {
                KillTree(process);
            }
            string message = exception is OperationCanceledException ? "Process or output-stream deadline exceeded." : exception.Message;
            return new ProcessResult(false, "", message, -1);
        }
    }

    // Keeps reading after the limit so a chatty child never blocks on a full pipe; only the first megabyte is kept.
    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken cancellation)
    {
        var captured = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) > 0)
        {
            int room = MaximumCapturedCharacters - captured.Length;
            if (room > 0)
            {
                captured.Append(buffer, 0, Math.Min(count, room));
            }
        }
        return captured.ToString();
    }

    private static void KillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
                process.WaitForExit(1000);
            }
        }
        catch (InvalidOperationException)
        {
            // The process ended between the check and the kill; there is nothing left to stop.
        }
    }
}
