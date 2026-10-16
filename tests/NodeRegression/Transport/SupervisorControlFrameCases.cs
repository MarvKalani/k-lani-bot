using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Guard;
using KLaniBot.Transport;
using KLaniBot.Visibility;

// The supervisor ended the only actuator whenever a control round was interrupted, including when the
// HTTP client simply went away. These cases script the actuator side so that timing is exact.
internal static class SupervisorControlFrameCases
{
    public static async Task<int> RunAsync(string temporaryRoot)
    {
        var checks = new Checks("Supervisor control frames");
        using var scenario = new ControlScenario(checks, temporaryRoot);

        await scenario.CheckClientGoneBeforeRoundAsync();
        await scenario.CheckClientGoneDuringRoundAsync();
        await scenario.CheckActionInFlightAndRefusedDetailsAsync();
        await scenario.CheckAfterEndAsync();
        return checks.Passed + await RunDeadlineCaseAsync(scenario.Policy, scenario.Caller, temporaryRoot);
    }

    // One supervisor in front of a scripted actuator. The steps count the lines written so far, so they run in this order.
    private sealed class ControlScenario : IDisposable
    {
        private readonly Checks _checks;
        private readonly SessionPresentation _presentation =
            SessionPresentation.Create("Test client", "Synthetic task", "master-fixture", "node-fixture", "observe");
        private readonly ScriptedActuatorChannel _channel = new();
        private readonly WorkerSupervisor _supervisor;

        public ControlScenario(Checks checks, string temporaryRoot)
        {
            _checks = checks;
            string state = Path.Combine(temporaryRoot, "supervisor-state");
            string workspace = Path.Combine(temporaryRoot, "supervisor-workspace");
            Directory.CreateDirectory(state);
            Directory.CreateDirectory(workspace);
            Policy = new NodePolicy
            {
                BackupRequirement = BackupRequirement.Off,
                StateRoot = state,
                WorkspaceRoot = workspace,
                Principals =
                [
                    new PrincipalPolicy(
                        "operator",
                        new string('a', 64),
                        [Scope.Observe, Scope.FilesWrite],
                        [],
                        DateTimeOffset.UtcNow.AddYears(1))
                    {
                        UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop }
                    }
                ]
            };
            _supervisor = new WorkerSupervisor(Policy,
                "unused-policy.json", TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(300), _channel);
        }

        public NodePolicy Policy
        {
            get;
        }

        public RequestContext Caller
        {
            get;
        } = new("operator", Guid.CreateVersion7().ToString());

        public void Dispose()
        {
            _supervisor.Dispose();
        }

        public async Task CheckClientGoneBeforeRoundAsync()
        {
            var cancelledBefore = new CancellationTokenSource();
            cancelledBefore.Cancel();
            bool cancelledQuietly = false;
            try
            {
                await _supervisor.NotifySessionAsync(Caller, true, cancelledBefore.Token);
            }
            catch (OperationCanceledException)
            {
                cancelledQuietly = true;
            }
            _checks.That(
                cancelledQuietly && _channel.Written.Count == 0 && _channel.TerminateCalls == 0,
                "a client that is already gone costs the actuator nothing");

            for (int attempt = 0; attempt < 100; attempt++)
            {
                try
                {
                    await _supervisor.SetSessionPresentationAsync(Caller, _presentation, cancelledBefore.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }
            _checks.That(_channel.Written.Count == 0 && _channel.TerminateCalls == 0, "100 abandoned requests leave the actuator running");
        }

        public async Task CheckClientGoneDuringRoundAsync()
        {
            _channel.HoldNextAnswer();
            var duringExchange = new CancellationTokenSource();
            var heartbeat = _supervisor.NotifySessionAsync(Caller, true, duringExchange.Token);
            await _channel.WaitUntilWrittenAsync(1);
            duringExchange.Cancel();
            _channel.ReleaseHeldAnswer("{\"success\":true}");
            var heartbeatResult = await heartbeat;
            _checks.That(heartbeatResult["success"]?.GetValue<bool>() == true && _channel.TerminateCalls == 0,
                "a client that leaves during the round does not end the actuator");

            _channel.HoldNextAnswer();
            var presentationCancel = new CancellationTokenSource();
            var presentationTask = _supervisor.SetSessionPresentationAsync(Caller, _presentation, presentationCancel.Token);
            await _channel.WaitUntilWrittenAsync(2);
            presentationCancel.Cancel();
            _channel.ReleaseHeldAnswer("{\"success\":true}");
            await presentationTask;
            _checks.That(_channel.TerminateCalls == 0, "the same holds for the session details sent before every request");

            var next = await _supervisor.NotifySessionAsync(Caller, true, CancellationToken.None);
            _checks.That(next["success"]?.GetValue<bool>() == true && _channel.Written.Count == 3,
                "the next round reads its own answer, the stream stayed in step");
        }

        public async Task CheckActionInFlightAndRefusedDetailsAsync()
        {
            _channel.HoldNextAnswer();
            var capabilityCancel = new CancellationTokenSource();
            JsonObject capabilityRequest = Request("get_machine_capabilities", new JsonObject());
            var capabilityTask = _supervisor.ExecuteAsync(capabilityRequest, Caller, capabilityCancel.Token);
            await _channel.WaitUntilWrittenAsync(4);
            capabilityCancel.Cancel();
            _channel.ReleaseHeldAnswer("{\"success\":true,\"outcome\":\"completed\"}");
            var capabilityResult = await capabilityTask;
            _checks.That(capabilityResult["success"]?.GetValue<bool>() == true
                && _channel.TerminateCalls == 0, "an action in flight is also completed when its client leaves");

            _channel.AnswerWith("{\"success\":false}");
            bool rejectionRaised = false;
            try
            {
                await _supervisor.SetSessionPresentationAsync(Caller, _presentation, CancellationToken.None);
            }
            catch (IOException)
            {
                rejectionRaised = true;
            }
            _checks.That(rejectionRaised && _channel.TerminateCalls == 1, "an actuator that refuses its own indicator data is ended");
        }

        public async Task CheckAfterEndAsync()
        {
            JsonObject management = await _supervisor.ExecuteAsync(Request("get_machine_capabilities", new JsonObject()),
                Caller, CancellationToken.None);
            _checks.That(management["success"]?.GetValue<bool>() == true
                && management["management"]?["worker_available"]?.GetValue<bool>() == false && _channel.Written.Count == 5,
                "authenticated diagnostics remain available after the desktop worker has stopped, without writing another frame");
            var afterStop = await _supervisor.NotifySessionAsync(Caller, true, CancellationToken.None);
            _checks.That(
                afterStop["code"]?.ToString() == "worker_stopped" && _channel.Written.Count == 5,
                "after the actuator ended, nothing more is written");

            string neverSent = Guid.CreateVersion7().ToString();
            var refused = await _supervisor.ExecuteAsync(Request("fs_write_file", new JsonObject
            {
                ["path"] = "never.txt",
                ["content"] = "x",
                ["request_id"] = neverSent
            }), Caller, CancellationToken.None);
            _checks.That(
                refused["code"]?.ToString() == "worker_stopped" && refused["outcome"]?.ToString() == "not_dispatched",
                "a mutation after the end is refused as not dispatched");
            var status = await _supervisor.ExecuteAsync(
                Request("get_action_status", new JsonObject { ["request_id"] = neverSent }),
                Caller,
                CancellationToken.None);
            _checks.That(status["code"]?.ToString() == "unknown_operation",
                "a request that was never sent leaves no pending record behind");
        }
    }

    // A round that does not finish in time must end the actuator, because its stream is out of step.
    private static async Task<int> RunDeadlineCaseAsync(NodePolicy policy, RequestContext caller, string temporaryRoot)
    {
        string state = Path.Combine(temporaryRoot, "supervisor-deadline-state");
        Directory.CreateDirectory(state);
        var deadlinePolicy = new NodePolicy
        {
            BackupRequirement = BackupRequirement.Off,
            StateRoot = state,
            WorkspaceRoot = policy.WorkspaceRoot,
            Principals = policy.Principals
        };
        var channel = new ScriptedActuatorChannel();
        using var supervisor = new WorkerSupervisor(
            deadlinePolicy,
            "unused-policy.json",
            TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(150),
            channel);
        channel.NeverAnswer();
        var result = await supervisor.NotifySessionAsync(caller, true, CancellationToken.None);
        bool ended = channel.TerminateCalls == 1 && result["code"]?.ToString() == "indicator_unavailable";
        if (!ended)
        {
            throw new Exception("Supervisor control frames: a round that exceeds its deadline must end the actuator");
        }
        return 1;
    }

    private static JsonObject Request(string action, JsonObject args)
    {
        return new JsonObject
        {
            ["action"] = action,
            ["args"] = args
        };
    }
}

// Plays the actuator's side of the lockstep protocol. A test decides when each answer arrives.
internal sealed class ScriptedActuatorChannel : IActuatorChannel
{
    private readonly object _sync = new();
    private TaskCompletionSource<string?>? _held;
    private TaskCompletionSource<string?>? _releasable;
    private string _fixedAnswer = "{\"success\":true}";
    private bool _neverAnswer;

    public List<string> Written { get; } = new();

    public int TerminateCalls
    {
        get; private set;
    }

    public bool HasExited => TerminateCalls > 0;

    public int ExitCode => 0;

    public void HoldNextAnswer()
    {
        _held = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _releasable = _held;
    }

    public void ReleaseHeldAnswer(string answer)
    {
        _releasable!.SetResult(answer);
    }

    public void AnswerWith(string answer)
    {
        _fixedAnswer = answer;
    }

    public void NeverAnswer()
    {
        _neverAnswer = true;
    }

    public async Task WaitUntilWrittenAsync(int count)
    {
        for (int attempt = 0; attempt < 400; attempt++)
        {
            lock (_sync)
            {
                if (Written.Count >= count)
                {
                    return;
                }
            }
            await Task.Delay(5);
        }
        throw new TimeoutException("The supervisor never wrote frame " + count + ".");
    }

    public Task WriteLineAsync(string line, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        lock (_sync)
        {
            Written.Add(line);
        }
        return Task.CompletedTask;
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellation)
    {
        if (_neverAnswer)
        {
            await Task.Delay(Timeout.Infinite, cancellation);
        }
        var held = _held;
        if (held != null)
        {
            _held = null;
            // The scripted actuator answers on its own schedule; only the supervisor's own deadline may interrupt the wait.
            return await held.Task.WaitAsync(cancellation);
        }
        return _fixedAnswer;
    }

    public Task WaitForExitAsync(CancellationToken cancellation)
    {
        return Task.Delay(Timeout.Infinite, cancellation);
    }

    public void Terminate()
    {
        TerminateCalls++;
    }

    public string? DescribeFailure()
    {
        return null;
    }

    public void Dispose()
    {
    }
}
