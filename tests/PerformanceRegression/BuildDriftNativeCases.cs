using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Guard;
using KLaniBot.Platforms.Linux;
using KLaniBot.Transport.CompactUi;

// Use case: the private GUI regression proves the build-drift report against real GTK windows read through the real Linux backend.
// A function is learned for the explicitly synthetic build label A of the lab fixture; the same fixture labelled B then needs a test
// run, and another fixture screen labelled B needs repair of the read step. No input is sent; the labels are not real releases.
internal sealed class BuildDriftNativeCases : IDisposable
{
    private const string FunctionName = "read-process-profile";
    private const string ReadTarget = "Process profile edit";
    private const string FirstBuild = "synthetic-drift-build-a";
    private const string SecondBuild = "synthetic-drift-build-b";
    private const int InfoAttempts = 100;
    private const int InfoDelayMilliseconds = 100;
    private const string WindowListTool = "wmctrl";

    private readonly string _fixturePath;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "k-lani-drift-" + Guid.NewGuid());
    private readonly ActionDispatcher _dispatcher;
    private readonly CompactStorageRoots _roots;
    private int _passed;

    private BuildDriftNativeCases(string fixturePath)
    {
        _fixturePath = Path.GetFullPath(fixturePath);
        Directory.CreateDirectory(_directory);
        _roots = new(Path.Combine(_directory, "library"), Path.Combine(_directory, "learning"));
        Directory.CreateDirectory(_roots.Library);
        _dispatcher = new ActionDispatcher(new LinuxUiService(), ObserveOnlyPolicy(_directory));
    }

    // A failed check ends with its message and exit code 1 instead of an unhandled exception and a core dump.
    public static async Task<int> RunAsync(string fixturePath)
    {
        try
        {
            PrivateGuiSession.Require();
            if (!OnPath(WindowListTool))
            {
                Console.WriteLine("SKIP native build drift: the Linux window list needs wmctrl in the private session.");
                return 0;
            }
            using var cases = new BuildDriftNativeCases(fixturePath);
            await cases.CheckAsync();
            Console.WriteLine($"{cases._passed} native build-drift checks passed.");

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Regression failed: " + exception);

            return 1;
        }
    }

    private async Task CheckAsync()
    {
        string document;
        using (var editor = StartFixture("fixture", "model-editor"))
        {
            await LearnReadAsync(editor.Id);
            document = DocumentHash();
            var report = await OtherBuildAsync(editor.Id);
            Check(Names(report, "test_run_needed").Contains(FunctionName),
                "the unchanged real window resolves every stored target, so the function needs a test run");
            await RefusedAsync(editor.Id);
            Stop(editor);
        }
        using (var form = StartFixture("dynamic-form", Path.Combine(_directory, "result.json")))
        {
            var report = await OtherBuildAsync(form.Id);
            var entry = (report["repair_needed"] as JsonArray ?? []).OfType<JsonObject>()
                .SingleOrDefault(item => item["name"]?.ToString() == FunctionName);
            Check(entry?["reason"]?.ToString() == "target_absent_or_ambiguous" && entry["label"]?.ToString() == "read " + ReadTarget,
                "a real window without the stored read target names the read step to repair");
            Stop(form);
        }
        Check(DocumentHash() == document, "the probes leave the stored document of the first label unchanged");
    }

    // The read function is learned without input: it observes the real entry and stores its selector for build label A.
    private async Task LearnReadAsync(int process)
    {
        using var session = Session(process, FirstBuild);
        await InfoAsync(session, process);
        var observed = await session.CallAsync("ui_observe", new JsonObject { ["pid"] = process, ["limit"] = 200 }, CancellationToken.None);
        string reference = observed["elements"]!.AsArray().OfType<JsonObject>()
            .Single(element => element["name"]?.ToString() == ReadTarget)["ref"]!.ToString();
        var learned = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = "Programming.learn_read",
            ["arguments"] = new JsonObject
            {
                ["pid"] = process,
                ["name"] = FunctionName,
                ["topic"] = "Use/Reads",
                ["ref"] = reference,
                ["field"] = "value",
                ["output"] = "profile"
            }
        }, CancellationToken.None);
        Check(learned["success"]?.ToString() == "true", "the read function is learned for the first label: " + learned.ToJsonString());
    }

    private async Task<JsonObject> OtherBuildAsync(int process)
    {
        using var session = Session(process, SecondBuild);
        var row = await InfoAsync(session, process);
        Check(!row.ContainsKey("functions") && row["other_builds"] is JsonArray { Count: 1 },
            "the second label lists no callable function and one other-build report: " + row.ToJsonString());
        var report = row["other_builds"]![0]!.AsObject();
        Check(report["build"]?.ToString() == FirstBuild && report["evidence_ref"] != null, "the report names the first label");
        Console.WriteLine("other_builds report: " + report.ToJsonString());

        return report;
    }

    private async Task RefusedAsync(int process)
    {
        using var session = Session(process, SecondBuild);
        await InfoAsync(session, process);
        var call = await session.CallAsync("cartridge_call", new JsonObject
        {
            ["function"] = FunctionName,
            ["arguments"] = new JsonObject { ["pid"] = process }
        }, CancellationToken.None);
        Check(call["success"]?.ToString() == "false", "the function of the first label stays refused for the second label");
    }

    // The fixture window can take a moment to appear on the private display; ui_info is read until its row is there.
    private static async Task<JsonObject> InfoAsync(CompactGatewaySession session, int process)
    {
        JsonObject info = new();
        for (int attempt = 0; attempt < InfoAttempts; attempt++)
        {
            info = await session.CallAsync("ui_info", new(), CancellationToken.None);
            var row = (info["windows"] as JsonArray ?? []).OfType<JsonObject>()
                .FirstOrDefault(window => window["pid"]?.GetValue<int>() == process);
            if (row != null)
            {
                return row;
            }
            await Task.Delay(InfoDelayMilliseconds);
        }

        throw new InvalidOperationException("Build drift: the fixture window never appeared in ui_info: " + info.ToJsonString());
    }

    private CompactGatewaySession Session(int process, string build)
    {
        var client = new SyntheticBuildClient(_dispatcher, process, build);

        return new CompactGatewaySession(client, "private-drift", _roots, () => "2025-11-25");
    }

    // A framework-dependent fixture runs through dotnet; a self-contained one, as a lab image carries it, runs directly.
    private Process StartFixture(string mode, string argument)
    {
        bool managed = _fixturePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var start = new ProcessStartInfo(managed ? "dotnet" : _fixturePath) { UseShellExecute = false };
        if (managed)
        {
            start.ArgumentList.Add(_fixturePath);
        }
        start.ArgumentList.Add(mode);
        start.ArgumentList.Add(argument);

        return Process.Start(start)!;
    }

    private static void Stop(Process fixture)
    {
        if (!fixture.HasExited)
        {
            fixture.Kill(true);
            fixture.WaitForExit();
        }
    }

    private static bool OnPath(string tool)
    {
        string[] directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries);

        return directories.Any(directory => File.Exists(Path.Combine(directory, tool)));
    }

    private string DocumentHash()
    {
        string file = Directory.GetFiles(_roots.Library, "generated-ui.json", SearchOption.AllDirectories).Single();

        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    }

    private static string[] Names(JsonObject report, string list)
    {
        return (report[list] as JsonArray ?? []).Select(item => item is JsonObject entry ? entry["name"]!.ToString() : item!.ToString())
            .ToArray();
    }

    private void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Build drift: " + message);
        }
        _passed++;
    }

    // The probe needs only observation; a policy without input scopes also proves that no input can be sent.
    private static NodePolicy ObserveOnlyPolicy(string directory)
    {
        string workspace = Path.Combine(directory, "workspace");
        string state = Path.Combine(directory, "state");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(state);
        var policy = new NodePolicy
        {
            BackupRequirement = BackupRequirement.Off,
            WorkspaceRoot = workspace,
            StateRoot = state,
            Principals =
            [
                new(SyntheticBuildClient.Principal, UnusedTokenHash(), [Scope.Observe], [], DateTimeOffset.UtcNow.AddHours(1))
                {
                    UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop }
                }
            ]
        };
        string file = Path.Combine(state, "policy.json");
        File.WriteAllText(file, JsonSerializer.Serialize(policy));

        return NodePolicy.Load(file);
    }

    // The client calls the dispatcher in process, so no token is ever presented; the hash only fills the principal record.
    private static string UnusedTokenHash()
    {
        byte[] token = Encoding.UTF8.GetBytes(Guid.NewGuid().ToString());

        return Convert.ToHexStringLower(SHA256.HashData(token));
    }

    public void Dispose()
    {
        _dispatcher.Dispose();
        Directory.Delete(_directory, true);
    }
}

// The node client of the native drift check: it calls the real dispatcher in process and shows only the fixture's windows, under one
// process name and an explicitly synthetic build label, because a dotnet-hosted fixture has no package build of its own.
internal sealed class SyntheticBuildClient(ActionDispatcher dispatcher, int fixtureProcess, string build) : ICompactNodeClient
{
    public const string Principal = "drift-test";
    private const string ProcessName = "k-lani-drift-fixture";
    private readonly RequestContext _context = new(Principal, new string('d', 32));

    public Task<JsonObject> CallAsync(Tool tool, JsonObject arguments, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var frame = new JsonObject { ["action"] = ToolNames.Of(tool), ["args"] = arguments.DeepClone() };
        JsonObject result = dispatcher.Execute(frame, _context);
        if (tool == Tool.ListWindows && result["windows"] is JsonArray windows)
        {
            var fixtureWindows = windows.OfType<JsonObject>().Where(window => window["ProcessId"]?.GetValue<int>() == fixtureProcess);
            result["windows"] = new JsonArray(fixtureWindows.Select(Relabel).ToArray());
        }

        return Task.FromResult(result);
    }

    private JsonNode Relabel(JsonObject window)
    {
        var relabelled = window.DeepClone().AsObject();
        relabelled["ProcessName"] = ProcessName;
        relabelled["AppVersion"] = build;

        return relabelled;
    }
}
