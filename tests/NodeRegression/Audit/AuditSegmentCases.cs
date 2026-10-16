using System.Text;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Audit;

// The journal is split into sealed segments so that a node started after a year of work does not read and check
// every entry ever written. These cases pin what start-up still has to catch, what it leaves to the full check,
// and that operations inside the retention window are still remembered across a segment boundary.
internal static class AuditSegmentCases
{
    private const long SmallSegmentBytes = 2048;

    // Time the segment cases set directly, so entries fall inside or outside the retention window on purpose.
    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }
    }

    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Audit segments");
        var scenario = new SegmentScenario(Path.Combine(temporaryRoot, "audit-segments"));

        CheckRotationAndRestart(checks, scenario);
        CheckAgedSegment(checks, scenario);
        // Operations inside the window are remembered even when they lie in a sealed segment.
        string straddling = scenario.Fresh();
        scenario.Fill(scenario.Open(straddling), 1, 60);
        scenario.Clock.Now = scenario.Clock.Now.AddHours(2);
        checks.That(scenario.Open(straddling).Operations.Count == 60, "operations in sealed segments inside the window are remembered");
        CheckMissingParts(checks, scenario);
        return checks.Passed;
    }

    // Small segments, a manual clock and one numbered directory per case. The clock only moves forward, so later
    // cases see the time earlier ones left.
    private sealed class SegmentScenario(string root)
    {
        private int _scenario;

        public ManualClock Clock
        {
            get;
        } = new(new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero));

        public string Fresh()
        {
            string directory = Path.Combine(root, "case-" + ++_scenario);
            Directory.CreateDirectory(directory);
            return directory;
        }

        public AuditJournal Open(string directory)
        {
            return new AuditJournal(directory, TimeSpan.FromHours(24), Clock, SmallSegmentBytes, verificationOnly: false);
        }

        public JsonObject Operation(int index)
        {
            string requestId = Guid.CreateVersion7(Clock.Now).ToString();
            return new JsonObject
            {
                ["operation_key"] = "operator:" + requestId,
                ["request_hash"] = "hash" + index,
                ["outcome"] = "verified",
                ["timestamp"] = Clock.Now.ToString("O")
            };
        }

        public void Fill(AuditJournal journal, int first, int last)
        {
            for (int index = first; index <= last; index++)
            {
                journal.Append(Operation(index));
            }
        }
    }

    private static string[] Segments(string directory)
    {
        return Directory.GetFiles(directory, "audit-*.jsonl").Order(StringComparer.Ordinal).ToArray();
    }

    private static bool Rejects(Func<object> open, string expectedText)
    {
        try
        {
            open();
            return false;
        }
        catch (InvalidDataException exception)
        {
            return exception.Message.Contains(expectedText, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void CheckRotationAndRestart(Checks checks, SegmentScenario scenario)
    {
        string rotating = scenario.Fresh();
        var journal = scenario.Open(rotating);
        scenario.Fill(journal, 1, 60);
        string[] sealedSegments = Segments(rotating);
        checks.That(sealedSegments.Length >= 2, "the journal is split into sealed segments once a segment is full");
        checks.That(
            sealedSegments.All(segment => File.ReadLines(segment).Last().Contains("audit_segment_sealed", StringComparison.Ordinal)),
            "every sealed segment ends with its signed seal");
        long written = journal.Sequence;
        var reopened = scenario.Open(rotating);
        checks.That(reopened.Sequence == written && reopened.Operations.Count == 60, "a restart continues the chain across segments");
        reopened.Append(scenario.Operation(61));
        // The append may seal a full segment first, which is an entry of its own.
        long afterRestart = reopened.Sequence;
        checks.That(afterRestart > written && scenario.Open(rotating).Sequence == afterRestart,
            "entries written after a restart extend the same chain");
        checks.That(AuditJournal.VerifyAll(rotating) == afterRestart, "the full check reads every segment");

        // The full check only reads: a checkpoint that lags behind the journal (a stop between the two writes) stays as it is.
        string checkpointFile = Path.Combine(rotating, "audit.checkpoint");
        byte[] lagging = File.ReadAllBytes(checkpointFile);
        reopened.Append(scenario.Operation(62));
        File.WriteAllBytes(checkpointFile, lagging);
        AuditJournal.VerifyAll(rotating);
        checks.That(File.ReadAllBytes(checkpointFile).SequenceEqual(lagging),
            "the full check writes nothing, not even a checkpoint catch-up");
    }

    // An old segment outside the window is only checked at its boundaries at start-up, but fully by VerifyAll.
    private static void CheckAgedSegment(Checks checks, SegmentScenario scenario)
    {
        string aged = scenario.Fresh();
        var old = scenario.Open(aged);
        scenario.Fill(old, 1, 60);
        scenario.Clock.Now = scenario.Clock.Now.AddDays(3);
        scenario.Fill(old, 61, 70);
        string firstSegment = Segments(aged)[0];
        string[] lines = File.ReadAllLines(firstSegment);
        lines[2] = lines[2].Replace("\"hash3\"", "\"hashX\"", StringComparison.Ordinal);
        File.WriteAllLines(firstSegment, lines);

        var agedReopened = scenario.Open(aged);
        checks.That(agedReopened.Operations.Count == 10, "start-up remembers only operations inside the window");
        checks.That(Rejects(() => AuditJournal.VerifyAll(aged), "integrity"), "the full check finds a change inside an old segment");
    }

    // A missing middle segment, a shortened active segment and a removed newest segment are all refused at start-up.
    private static void CheckMissingParts(Checks checks, SegmentScenario scenario)
    {
        string gap = scenario.Fresh();
        scenario.Fill(scenario.Open(gap), 1, 100);
        string[] gapSegments = Segments(gap);
        checks.That(gapSegments.Length >= 3, "enough segments for a gap");
        File.Delete(gapSegments[1]);
        checks.That(Rejects(() => scenario.Open(gap), "missing"), "a missing segment is refused");

        string cut = scenario.Fresh();
        scenario.Fill(scenario.Open(cut), 1, 60);
        string active = Path.Combine(cut, "audit.jsonl");
        string[] activeLines = File.ReadAllLines(active);
        File.WriteAllLines(active, activeLines.Take(Math.Max(0, activeLines.Length - 1)), new UTF8Encoding(false));
        checks.That(Rejects(() => scenario.Open(cut), "shortened"), "a shortened active segment is refused");

        // With the active segment gone too, the journal still ends before the checkpoint.
        string removedTail = scenario.Fresh();
        scenario.Fill(scenario.Open(removedTail), 1, 60);
        File.Delete(Path.Combine(removedTail, "audit.jsonl"));
        File.Delete(Segments(removedTail)[^1]);
        checks.That(Rejects(() => scenario.Open(removedTail), "shortened"), "removing the newest segment is noticed");
    }
}
