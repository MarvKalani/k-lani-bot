using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Actions;
using KLaniBot.Audit;
using KLaniBot.Guard;

// The operation ledger must let a node run indefinitely and still never perform the same request twice.
// The first scenario reproduces the review finding: after 2048 mutations every further mutation was refused.
internal static class OperationLedgerCases
{
    private const int MutationsPastOldCap = 2100;
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);
    private static readonly TimeSpan BeyondWindow = TimeSpan.FromHours(25);

    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Operation ledger");
        var start = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
        var clock = new ManualClock(start);

        CheckUuidTimes(checks, start);
        CheckWindowAndCapacity(checks, start, clock);
        var fixture = new LedgerFixture(temporaryRoot);
        string lastRequest = CheckManyMutations(checks, fixture, clock);
        CheckRestartAndClockEdges(checks, fixture, clock, lastRequest);
        CheckAfterWindow(checks, fixture, clock, lastRequest);
        return checks.Passed;
    }

    private static void CheckUuidTimes(Checks checks, DateTimeOffset start)
    {
        checks.That(
            Uuid7Time.TryReadMilliseconds(NewUuid7(start), out long readMilliseconds)
            && readMilliseconds == start.ToUnixTimeMilliseconds(),
            "the creation time of a UUIDv7 is read back exactly");
        checks.That(!Uuid7Time.TryReadMilliseconds(Guid.NewGuid().ToString(), out _), "a random UUID has no creation time");
        checks.That(!Uuid7Time.TryReadMilliseconds("not-a-uuid", out _), "text is not a creation time");
    }

    private static void CheckWindowAndCapacity(Checks checks, DateTimeOffset start, ManualClock clock)
    {
        bool windowRejected = false;
        try
        {
            _ = new OperationLedger(TimeSpan.FromMinutes(30), clock);
        }
        catch (ArgumentOutOfRangeException)
        {
            windowRejected = true;
        }
        checks.That(windowRejected, "a window shorter than one hour is refused");

        var ledger = new OperationLedger(Window, clock);
        var recordedResult = new JsonObject();
        for (int index = 0; index < OperationLedger.MaximumEntries; index++)
        {
            ledger.Remember("operator:" + NewUuid7(start), "hash", recordedResult);
        }
        ActionErrorCode? capacityCode = null;
        try
        {
            ledger.RequireAdmissible(NewUuid7(start));
        }
        catch (OperationWindowException exception)
        {
            capacityCode = exception.Code;
        }
        checks.That(capacityCode == ActionErrorCode.OperationLedgerFull, "a full ledger refuses new work before anything starts");
        clock.Advance(BeyondWindow);
        ledger.RequireAdmissible(NewUuid7(clock.GetUtcNow()));
        checks.That(ledger.Count == 0, "entries older than the window are dropped when the next request is admitted");
        clock.Advance(-BeyondWindow);
    }

    // Returns the last request id, which the later steps replay.
    private static string CheckManyMutations(Checks checks, LedgerFixture fixture, ManualClock clock)
    {
        var quietError = Console.Error;
        int failures = 0;
        string lastRequest = "";
        var dispatcher = new ActionDispatcher(fixture.Ui, fixture.Policy, clock: clock);
        try
        {
            // Every receipt is also printed to stderr; thousands of them would only bury the test result.
            Console.SetError(TextWriter.Null);
            for (int index = 0; index < MutationsPastOldCap; index++)
            {
                lastRequest = NewUuid7(clock.GetUtcNow());
                if (fixture.Launch(dispatcher, lastRequest)["success"]?.GetValue<bool>() != true)
                {
                    failures++;
                }
            }
        }
        finally
        {
            Console.SetError(quietError);
        }
        checks.That(failures == 0 && fixture.Ui.LaunchCalls == MutationsPastOldCap,
            "more than 2048 mutations in one run are all executed (reproduced review finding)");

        var replay = fixture.Launch(dispatcher, lastRequest);
        checks.That(replay["replayed"]?.GetValue<bool>() == true
            && fixture.Ui.LaunchCalls == MutationsPastOldCap,
                "a retry inside the window gets the recorded answer without a second OS action");
        dispatcher.Dispose();
        return lastRequest;
    }

    private static void CheckRestartAndClockEdges(Checks checks, LedgerFixture fixture, ManualClock clock, string lastRequest)
    {
        var restarted = new ActionDispatcher(fixture.Ui, fixture.Policy, clock: clock);
        var replayAfterRestart = fixture.Launch(restarted, lastRequest);
        checks.That(replayAfterRestart["replayed"]?.GetValue<bool>() == true
            && fixture.Ui.LaunchCalls == 2100, "a retry after a restart is still recognised");
        var newWorkAfterRestart = fixture.Launch(restarted, NewUuid7(clock.GetUtcNow()));
        checks.That(newWorkAfterRestart["success"]?.GetValue<bool>() == true
            && fixture.Ui.LaunchCalls == 2101, "a restarted node keeps working past the old cap");

        string expiredId = NewUuid7(clock.GetUtcNow() - BeyondWindow);
        var expired = fixture.Launch(restarted, expiredId);
        checks.That(
            expired["code"]?.ToString() == "request_id_expired"
            && expired["outcome"]?.ToString() == "not_dispatched"
            && fixture.Ui.LaunchCalls == 2101,
            "a request_id older than the window is refused before anything runs");
        bool admittedInJournal = File.ReadLines(Path.Combine(fixture.State, "audit.jsonl"))
            .Any(line => line.Contains(expiredId, StringComparison.Ordinal) && line.Contains("\"admitted\"", StringComparison.Ordinal));
        checks.That(!admittedInJournal, "a refused request_id is never admitted in the journal");
        var future = fixture.Launch(restarted, NewUuid7(clock.GetUtcNow() + TimeSpan.FromHours(1)));
        checks.That(
            future["code"]?.ToString() == "request_id_from_future" && fixture.Ui.LaunchCalls == 2101,
            "a request_id far in the future is refused");
        var slightlyAhead = fixture.Launch(restarted, NewUuid7(clock.GetUtcNow() + TimeSpan.FromMinutes(4)));
        checks.That(slightlyAhead["success"]?.GetValue<bool>() == true,
            "a small clock difference between controller and node is tolerated");
        restarted.Dispose();
    }

    private static void CheckAfterWindow(Checks checks, LedgerFixture fixture, ManualClock clock, string lastRequest)
    {
        clock.Advance(BeyondWindow);
        var journalAfterWindow = new AuditJournal(fixture.State, Window, clock);
        checks.That(journalAfterWindow.Operations.Count == 0, "a restart does not load operations that are outside the window");

        var afterWindow = new ActionDispatcher(fixture.Ui, fixture.Policy, clock: clock);
        var oldReplay = fixture.Launch(afterWindow, lastRequest);
        checks.That(
            oldReplay["code"]?.ToString() == "request_id_expired" && fixture.Ui.LaunchCalls == 2102,
            "an old id cannot be replayed once it left the window");
        afterWindow.Dispose();
    }

    // A node whose operator may launch one approved program; every launch is one mutation.
    private sealed class LedgerFixture
    {
        public LedgerFixture(string temporaryRoot)
        {
            State = Path.Combine(temporaryRoot, "ledger-state");
            string workspace = Path.Combine(temporaryRoot, "ledger-workspace");
            Directory.CreateDirectory(State);
            Directory.CreateDirectory(workspace);
            Application = Path.Combine(workspace, "approved-program");
            Policy = new NodePolicy
            {
                BackupRequirement = BackupRequirement.Off,
                StateRoot = State,
                WorkspaceRoot = workspace,
                Principals =
                [
                    new PrincipalPolicy(
                        "operator",
                        new string('a', 64),
                        [Scope.Observe, Scope.Process],
                        [Application],
                        DateTimeOffset.UtcNow.AddYears(1))
                    {
                        UiGuard = new UiGuardOptions { Mode = UiGuardMode.Desktop }
                    }
                ]
            };
        }

        public string State
        {
            get;
        }

        public string Application
        {
            get;
        }

        public NodePolicy Policy
        {
            get;
        }

        public FakeUi Ui
        {
            get;
        } = new();

        private RequestContext Caller
        {
            get;
        } = new("operator", Guid.CreateVersion7().ToString());

        public JsonObject Launch(ActionDispatcher dispatcher, string requestId)
        {
            var request = new JsonObject
            {
                ["action"] = "launch_process",
                ["args"] = new JsonObject
                {
                    ["path"] = Application,
                    ["request_id"] = requestId
                }
            };
            return dispatcher.Execute(request, Caller);
        }
    }

    private static string NewUuid7(DateTimeOffset time)
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        long milliseconds = time.ToUnixTimeMilliseconds();
        for (int index = 5; index >= 0; index--)
        {
            bytes[index] = (byte)(milliseconds & 0xFF);
            milliseconds >>= 8;
        }
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }
}

// Time that only moves when a case moves it, so the operation window can be crossed without waiting.
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }

    public void Advance(TimeSpan span)
    {
        _now += span;
    }
}
