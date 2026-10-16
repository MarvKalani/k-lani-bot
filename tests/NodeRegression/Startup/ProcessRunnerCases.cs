using System.Diagnostics;
using KLaniBot;
using KLaniBot.Common;

// A child started by the node must never share the actuator's standard input: in the stdio worker that
// stream is the private control channel from the supervisor.
internal static class ProcessRunnerCases
{
    public const string ProbeArgument = "--stdin-probe";

    public static int Run()
    {
        if (OperatingSystem.IsWindows())
        {
            return 0;
        }
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition)
            {
                throw new Exception("Process runner: " + name);
            }
            passed++;
        }
        var readsLine = ProcessRunner.Run("/bin/sh", ["-c", "read line; echo \"got:$line\""], 5000);
        Check(readsLine.Success && readsLine.Output.Trim() == "got:", "a child that reads standard input receives end of input");
        Check(
            StartProbeWithOpenStandardInput() == "ok",
            "a child does not block on an inherited, open standard input (reproduced review finding)");
        return passed;
    }

    // Runs inside a child process whose standard input is an open pipe that never delivers data, like the
    // control channel of a real actuator. Before the fix, cat inherited that pipe and waited for the deadline.
    public static int RunProbe()
    {
        var watch = Stopwatch.StartNew();
        var result = ProcessRunner.Run("/bin/cat", [], 3000);
        bool promptly = watch.Elapsed < TimeSpan.FromSeconds(2);
        Console.WriteLine(result.Success && promptly ? "ok" : "blocked");
        return 0;
    }

    private static string StartProbeWithOpenStandardInput()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true
        };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!) == "dotnet")
        {
            start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()!.Location);
        }
        start.ArgumentList.Add(ProbeArgument);
        using var probe = Process.Start(start)!;
        string answer = probe.StandardOutput.ReadToEnd().Trim();
        probe.WaitForExit();
        return answer;
    }
}
