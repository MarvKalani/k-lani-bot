using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Guard;
using KLaniBot.Platforms;
using KLaniBot.Platforms.Windows;

// Arguments for launch_process must reach the started program exactly as the agent sent them.
// The first defect here was a join-and-requote step that doubled every backslash.
internal static class LaunchArgumentCases
{
    private static readonly string[][] ArgumentSets =
    [
        ["plain"],
        ["with space"],
        [@"C:\Users\user\file.txt"],
        [@"C:\Users\user\a b.txt"],
        [@"C:\dir with space\"],
        [@"\\server\share\a b"],
        ["say \"hi\""],
        ["\\\"quoted after backslash"],
        ["trailing\\"],
        ["\\"],
        ["\""],
        [""],
        ["tab\there"],
        ["line1\nline2"],
        [" "],
        ["ünï cödé ✓"],
        ["first", @"C:\a b\", "", "last \"x\""]
    ];

    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Launch arguments");

        CheckQuoting(checks);
        using var fixture = new LaunchFixture(temporaryRoot, "launch", withArgumentForms: false);
        CheckDispatch(checks, fixture);
        using var restricted = new LaunchFixture(temporaryRoot, "launch-forms", withArgumentForms: true);
        CheckArgumentForms(checks, restricted);
        return checks.Passed;
    }

    private static void CheckQuoting(Checks checks)
    {
        foreach (var arguments in ArgumentSets)
        {
            string joined = WindowsCommandLine.Join(arguments);
            var parsed = ParseWindowsCommandLine(joined);
            checks.That(parsed.SequenceEqual(arguments), "Windows quoting round-trips " + string.Join(" | ", arguments));
        }
        checks.That(WindowsCommandLine.Join([]) == "", "no arguments produce an empty parameter string");
        if (OperatingSystem.IsLinux())
        {
            foreach (var arguments in ArgumentSets)
            {
                checks.That(StartAndCollect(arguments).SequenceEqual(arguments),
                    "operating system delivers " + string.Join(" | ", arguments));
            }
        }
    }

    private static void CheckDispatch(Checks checks, LaunchFixture fixture)
    {
        var sent = new JsonArray(ArgumentSets[^1].Select(argument => (JsonNode)JsonValue.Create(argument)!).ToArray());
        var accepted = fixture.Launch(sent);
        checks.That(accepted["success"]!.GetValue<bool>(), "allowed program with arguments is launched");
        checks.That(fixture.Ui.LaunchedPath == fixture.Application, "the resolved program path reaches the backend");
        checks.That(fixture.Ui.LaunchedArguments!.SequenceEqual(ArgumentSets[^1]), "the dispatcher passes the argument list unchanged");

        var nullByte = fixture.Launch(new JsonArray(JsonValue.Create("bad\0argument")));
        checks.That(nullByte["outcome"]!.ToString() == "not_dispatched", "a NUL character in an argument is rejected before launch");
        JsonNode[] manyArguments = Enumerable.Range(0, 101).Select(index => (JsonNode)JsonValue.Create("a" + index)!).ToArray();
        var tooMany = fixture.Launch(new JsonArray(manyArguments));
        checks.That(tooMany["outcome"]!.ToString() == "not_dispatched", "more than 100 arguments are rejected");
    }

    // Forms are matched whole and position by position; outside them nothing starts, and the refusal is "not dispatched".
    private static void CheckArgumentForms(Checks checks, LaunchFixture fixture)
    {
        checks.That(fixture.Launch(new JsonArray("--export", "model.3mf"))["success"]!.GetValue<bool>(),
            "an argument form allows its arguments");
        checks.That(fixture.Launch(new JsonArray())["success"]!.GetValue<bool>(), "an empty form allows no arguments");
        int launches = fixture.Ui.LaunchCalls;
        string[][] refused = [["--export", "model.3mf; rm -rf /"], ["--export"], ["-Command", "anything"], ["--export", "MODEL.3mf"]];
        foreach (string[] arguments in refused)
        {
            JsonNode[] values = arguments.Select(argument => (JsonNode)JsonValue.Create(argument)!).ToArray();
            JsonObject result = fixture.Launch(new JsonArray(values));
            checks.That(result["outcome"]!.ToString() == "not_dispatched",
                "arguments outside every form are refused: " + string.Join(' ', arguments));
        }
        checks.That(fixture.Ui.LaunchCalls == launches, "a refused launch never reaches the backend");

        var principal = new PrincipalPolicy("check", new string('b', 64), [Scope.Process], ["/opt/listed"], DateTimeOffset.UtcNow)
        {
            ApplicationArguments = new() { ["/opt/unlisted"] = [["x"]] }
        };
        checks.That(Throws(() => ArgumentPatterns.Validate(principal)), "forms for a program that is not listed are refused");
        principal = principal with
        {
            ApplicationArguments = new()
            {
                ["/opt/listed"] = [["("]]
            }
        };
        checks.That(Throws(() => ArgumentPatterns.Validate(principal)), "an invalid pattern is refused when the policy is read");
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }

    // A principal that may launch one approved program, with a dispatcher in front of a fake desktop.
    private sealed class LaunchFixture : IDisposable
    {
        private readonly ActionDispatcher _dispatcher;
        private readonly RequestContext _caller = new("operator", Guid.CreateVersion7().ToString());

        // With argument forms, the program accepts "--export <name>.3mf" or no arguments at all.
        public LaunchFixture(string temporaryRoot, string name, bool withArgumentForms)
        {
            string state = Path.Combine(temporaryRoot, name + "-state");
            string workspace = Path.Combine(temporaryRoot, name + "-workspace");
            Directory.CreateDirectory(state);
            Directory.CreateDirectory(workspace);
            Application = Path.Combine(workspace, "approved-program");
            var policy = new NodePolicy
            {
                BackupRequirement = BackupRequirement.Off,
                StateRoot = state,
                WorkspaceRoot = workspace,
                Principals =
                [
                    new PrincipalPolicy(
                        "operator",
                        new string('a', 64),
                        [Scope.Observe, Scope.Process],
                        [Application],
                        DateTimeOffset.UtcNow.AddHours(1))
                    {
                        UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop },
                        ApplicationArguments = withArgumentForms
                            ? new() { [Application] = [["--export", "[a-z]+\\.3mf"], []] }
                            : new()
                    }
                ]
            };
            _dispatcher = new ActionDispatcher(Ui, policy);
        }

        public string Application
        {
            get;
        }

        public FakeUi Ui
        {
            get;
        } = new();

        public void Dispose()
        {
            _dispatcher.Dispose();
        }

        public JsonObject Launch(JsonArray arguments)
        {
            var request = new JsonObject
            {
                ["action"] = "launch_process",
                ["args"] = new JsonObject
                {
                    ["path"] = Application,
                    ["arguments"] = arguments,
                    ["request_id"] = Guid.CreateVersion7().ToString()
                }
            };
            return _dispatcher.Execute(request, _caller);
        }
    }

    // Starts a shell that prints each argument followed by a NUL byte, so any character except NUL
    // (which the contract forbids) can be compared exactly.
    private static string[] StartAndCollect(string[] arguments)
    {
        var start = ProcessLauncher.CreateStartInfo("/bin/sh", ["-c", "printf '%s\\0' \"$@\"", "sh", .. arguments]);
        start.RedirectStandardOutput = true;
        start.StandardOutputEncoding = new UTF8Encoding(false);
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var parts = output.Split('\0');
        return parts.Take(parts.Length - 1).ToArray();
    }

    // Reference implementation of the rules used by CommandLineToArgvW for arguments after the
    // program name. Written independently from WindowsCommandLine so the round trip is a real check.
    private static List<string> ParseWindowsCommandLine(string commandLine)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        bool inQuotes = false;
        bool hasArgument = false;
        int index = 0;
        while (index < commandLine.Length)
        {
            char character = commandLine[index];
            if (character == '\\')
            {
                index = AppendBackslashRun(commandLine, index, current);
                hasArgument = true;
                continue;
            }
            if (character == '"')
            {
                inQuotes = !inQuotes;
                hasArgument = true;
                index++;
                continue;
            }
            if ((character == ' ' || character == '\t') && !inQuotes)
            {
                hasArgument = EndArgument(arguments, current, hasArgument);
                index++;
                continue;
            }
            current.Append(character);
            hasArgument = true;
            index++;
        }
        EndArgument(arguments, current, hasArgument);
        return arguments;
    }

    // Whitespace outside quotes ends an argument, but only one that was started; returns that none is started now.
    private static bool EndArgument(List<string> arguments, StringBuilder current, bool hasArgument)
    {
        if (hasArgument)
        {
            arguments.Add(current.ToString());
            current.Clear();
        }
        return false;
    }

    // Backslashes count literally unless a quote follows: then each pair is one backslash, and an odd one escapes the quote.
    private static int AppendBackslashRun(string commandLine, int index, StringBuilder current)
    {
        int backslashes = 0;
        while (index < commandLine.Length && commandLine[index] == '\\')
        {
            backslashes++;
            index++;
        }

        if (index < commandLine.Length && commandLine[index] == '"')
        {
            current.Append('\\', backslashes / 2);
            if (backslashes % 2 == 1)
            {
                current.Append('"');
                index++;
            }
        }
        else
        {
            current.Append('\\', backslashes);
        }
        return index;
    }
}
