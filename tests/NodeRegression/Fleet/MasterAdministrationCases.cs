using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Fleet;

// The pairing regression once failed with "Connection reset by peer" while reading a master command's answer, under
// load and next to the transport suite. Several callers send commands at once here; before the fix about three in
// four were reset, and every one must get its own answer.
internal static class MasterAdministrationCases
{
    private const int Calls = 400;
    private const int Callers = 4;

    public static async Task<int> RunAsync(string temporaryRoot)
    {
        var checks = new Checks("Master administration");
        string directory = Path.Combine(temporaryRoot, "master-administration");
        Directory.CreateDirectory(directory);
        using var lifetime = new CancellationTokenSource();
        Task server = MasterAdministration.ServeAsync(
            directory,
            command => new JsonObject { ["success"] = true, ["echo"] = command },
            lifetime.Token);

        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        // Several callers at once, like the operator's console and a fleet command reaching the master together.
        await Task.WhenAll(Enumerable.Range(0, Callers).Select(caller => Task.Run(() => CallRepeatedlyAsync(directory, caller, failures))));
        lifetime.Cancel();
        await server.ContinueWith(_ => { }, TaskScheduler.Default);
        string examples = string.Join("; ", failures.Take(3));
        string name = Calls + " commands from " + Callers + " concurrent callers each get their answer";
        checks.That(failures.Count == 0, name + " (" + failures.Count + " failed: " + examples + ")");
        return checks.Passed;
    }

    private static async Task CallRepeatedlyAsync(
        string directory,
        int caller,
        System.Collections.Concurrent.ConcurrentBag<string> failures)
    {
        for (int call = 0; call < Calls / Callers; call++)
        {
            string command = "status " + caller + "-" + call;
            try
            {
                JsonObject answer = await MasterAdministration.CallAsync(directory, command, CancellationToken.None);
                if (answer["echo"]?.ToString() != command)
                {
                    failures.Add(command + ": wrong answer");
                }
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException)
            {
                failures.Add(command + ": " + exception.GetType().Name + " " + exception.Message);
            }
        }
    }
}
