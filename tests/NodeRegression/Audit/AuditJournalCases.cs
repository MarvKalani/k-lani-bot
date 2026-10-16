using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using KLaniBot;
using KLaniBot.Audit;
using KLaniBot.Common;

// The journal is evidence: removing its tail must be noticed, and its files must be private from the
// first byte. The first scenarios reproduce the defects found in the review of 2026-10-01.
internal static class AuditJournalCases
{
    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerAll = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint SetUmask(uint mask);

    public static int Run(string temporaryRoot)
    {
        var cases = new CaseDirectories(Path.Combine(temporaryRoot, "audit-cases"));
        var checks = new Checks("Audit journal");

        CheckCompleteJournal(checks, cases.Fresh());
        CheckTampering(checks, cases);
        CheckCheckpoints(checks, cases);
        // The node reads one journal format. A file of an earlier format is never read under rules that no longer hold; it is set
        // aside unchanged (EarlierJournalCases).
        string older = cases.Fresh();
        WriteVersionOneJournal(older, 3);
        _ = new AuditJournal(older);
        checks.That(File.Exists(Path.Combine(older, "audit-format-1.jsonl")), "a journal in an older format is set aside, not read");
        CheckConcurrentWriters(checks, cases.Fresh());
        CheckFailedWrite(checks, cases.Fresh());
        if (!OperatingSystem.IsWindows())
        {
            CheckPrivateModes(checks, cases.Fresh());
        }
        return checks.Passed;
    }

    // Each scenario gets its own numbered directory, so no journal sees another one's files.
    private sealed class CaseDirectories(string root)
    {
        private int _scenario;

        public string Fresh()
        {
            string directory = Path.Combine(root, "case-" + ++_scenario);
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    private static JsonObject Operation(int index)
    {
        return new JsonObject
        {
            ["operation_key"] = "operator:" + index,
            ["request_hash"] = "hash" + index,
            ["outcome"] = "verified"
        };
    }

    private static string BuildJournal(string directory, int entries)
    {
        var journal = new AuditJournal(directory);
        for (int index = 1; index <= entries; index++)
        {
            journal.Append(Operation(index));
        }
        return Path.Combine(directory, "audit.jsonl");
    }

    private static bool Rejects(string directory, string expectedText)
    {
        try
        {
            _ = new AuditJournal(directory);
            return false;
        }
        catch (InvalidDataException exception)
        {
            return exception.Message.Contains(expectedText, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void CheckCompleteJournal(Checks checks, string normal)
    {
        string normalJournal = BuildJournal(normal, 5);
        var reopened = new AuditJournal(normal);

        checks.That(reopened.Sequence == 5 && reopened.Operations.Count == 5, "a complete journal reopens with all operations");
        string[] lines = File.ReadAllLines(normalJournal);
        checks.That(lines[2].Contains("\"version\":2") && lines[2].Contains("\"seq\":3"), "entries carry their sequence number");
        checks.That(File.Exists(Path.Combine(normal, "audit.checkpoint")), "a checkpoint is written next to the journal");
    }

    // The first scenario reproduces a review finding: removing the last entries went unnoticed.
    private static void CheckTampering(Checks checks, CaseDirectories cases)
    {
        string truncated = cases.Fresh();
        string truncatedJournal = BuildJournal(truncated, 5);
        File.WriteAllLines(truncatedJournal, File.ReadAllLines(truncatedJournal).Take(2));
        checks.That(Rejects(truncated, "shortened"), "removing the last entries is detected (reproduced review finding)");

        string cutMidLine = cases.Fresh();
        string cutJournal = BuildJournal(cutMidLine, 3);
        byte[] cutBytes = File.ReadAllBytes(cutJournal);
        File.WriteAllBytes(cutJournal, cutBytes.Take(cutBytes.Length - 40).ToArray());
        checks.That(Rejects(cutMidLine, "not valid"), "a journal cut in the middle of an entry is refused");

        string rewritten = cases.Fresh();
        string rewrittenJournal = BuildJournal(rewritten, 4);
        string[] rewrittenLines = File.ReadAllLines(rewrittenJournal);
        rewrittenLines[1] = rewrittenLines[1].Replace("hash2", "hash9");
        File.WriteAllLines(rewrittenJournal, rewrittenLines);
        checks.That(Rejects(rewritten, "integrity"), "a changed payload breaks the chain");

        string reordered = cases.Fresh();
        string reorderedJournal = BuildJournal(reordered, 4);
        string[] reorderedLines = File.ReadAllLines(reorderedJournal);
        string secondLine = reorderedLines[1];
        reorderedLines[1] = reorderedLines[2];
        reorderedLines[2] = secondLine;
        File.WriteAllLines(reorderedJournal, reorderedLines);
        checks.That(Rejects(reordered, "integrity"), "reordered entries break the chain");
    }

    private static void CheckCheckpoints(Checks checks, CaseDirectories cases)
    {
        string missingCheckpoint = cases.Fresh();
        BuildJournal(missingCheckpoint, 3);
        File.Delete(Path.Combine(missingCheckpoint, "audit.checkpoint"));
        checks.That(Rejects(missingCheckpoint, "checkpoint missing"), "a deleted checkpoint is not silently recreated");

        string forgedCheckpoint = cases.Fresh();
        BuildJournal(forgedCheckpoint, 3);
        string checkpointPath = Path.Combine(forgedCheckpoint, "audit.checkpoint");
        File.WriteAllText(checkpointPath, File.ReadAllText(checkpointPath).Replace("\"seq\":3", "\"seq\":2"));
        checks.That(Rejects(forgedCheckpoint, "not authentic"), "a checkpoint changed without the key is refused");

        string removedJournal = cases.Fresh();
        string removedPath = BuildJournal(removedJournal, 3);
        File.Delete(removedPath);
        checks.That(Rejects(removedJournal, "shortened"), "deleting the whole journal is detected");

        string crashWindow = cases.Fresh();
        var crashJournal = new AuditJournal(crashWindow);
        for (int index = 1; index <= 3; index++)
        {
            crashJournal.Append(Operation(index));
        }
        string crashCheckpoint = Path.Combine(crashWindow, "audit.checkpoint");
        byte[] checkpointAfterThree = File.ReadAllBytes(crashCheckpoint);
        crashJournal.Append(Operation(4));
        crashJournal.Append(Operation(5));
        File.WriteAllBytes(crashCheckpoint, checkpointAfterThree);
        var recovered = new AuditJournal(crashWindow);
        checks.That(recovered.Sequence == 5, "a checkpoint behind the journal (stop between the two writes) is accepted");
        checks.That(!File.ReadAllBytes(crashCheckpoint).SequenceEqual(checkpointAfterThree), "the checkpoint catches up after such a stop");
    }

    private static void CheckConcurrentWriters(Checks checks, string parallel)
    {
        var parallelJournal = new AuditJournal(parallel);
        var writers = Enumerable.Range(0, 2).Select(writer => Task.Run(() =>
        {
            for (int index = 0; index < 300; index++)
            {
                parallelJournal.Append(Operation(writer * 1000 + index));
            }
        })).ToArray();
        Task.WaitAll(writers);

        checks.That(new AuditJournal(parallel).Sequence == 600, "concurrent writers keep one unbroken chain");
    }

    private static void CheckFailedWrite(Checks checks, string faulted)
    {
        var faultedJournal = new AuditJournal(faulted);
        string faultedPath = Path.Combine(faulted, "audit.jsonl");
        Directory.CreateDirectory(faultedPath);
        bool firstFailed = false;
        try
        {
            faultedJournal.Append(Operation(1));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            firstFailed = true;
        }
        Directory.Delete(faultedPath);

        bool refusedAfterFailure = false;
        try
        {
            faultedJournal.Append(Operation(2));
        }
        catch (InvalidOperationException)
        {
            refusedAfterFailure = true;
        }
        checks.That(firstFailed && refusedAfterFailure, "after a failed write the journal refuses further entries until restart");
    }

    // An open umask must not leak: every file is owner-only from its first byte.
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void CheckPrivateModes(Checks checks, string privateCase)
    {
        uint previousMask = SetUmask(0);
        string privateRoot = Path.Combine(privateCase, "state");
        try
        {
            PrivateFile.CreateDirectory(privateRoot);
            BuildJournal(privateRoot, 2);
            PrivateFile.WriteAllText(Path.Combine(privateRoot, "secret.txt"), "synthetic");
        }
        finally
        {
            SetUmask(previousMask);
        }

        checks.That(File.GetUnixFileMode(privateRoot) == OwnerAll, "a private directory is owner-only even with an open umask");
        checks.That(File.GetUnixFileMode(Path.Combine(privateRoot, "audit.key")) == OwnerReadWrite,
            "the audit key is owner-only from creation");
        checks.That(File.GetUnixFileMode(Path.Combine(privateRoot, "audit.jsonl")) == OwnerReadWrite,
            "the journal is owner-only from creation");
        checks.That(
            File.GetUnixFileMode(Path.Combine(privateRoot, "audit.checkpoint")) == OwnerReadWrite,
            "the checkpoint is owner-only from creation");
        checks.That(
            File.GetUnixFileMode(Path.Combine(privateRoot, "secret.txt")) == OwnerReadWrite,
            "private files are owner-only from creation");
        string loose = Path.Combine(privateRoot, "audit.jsonl");
        File.SetUnixFileMode(loose, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        new AuditJournal(privateRoot).Append(Operation(3));
        checks.That(File.GetUnixFileMode(loose) == OwnerReadWrite, "an existing readable journal is tightened on the next write");
    }

    // The format of the first release, which had neither sequence numbers nor a checkpoint.
    private static void WriteVersionOneJournal(string directory, int entries)
    {
        _ = new AuditJournal(directory);
        byte[] key = File.ReadAllBytes(Path.Combine(directory, "audit.key"));
        string head = "";
        var lines = new List<string>();
        for (int index = 1; index <= entries; index++)
        {
            var payload = new JsonObject
            {
                ["operation_key"] = "operator:" + index,
                ["request_hash"] = "hash" + index,
                ["outcome"] = "verified"
            };
            string signedText = head + "\n" + payload.ToJsonString();
            string signature = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(signedText)));
            var entry = new JsonObject
            {
                ["version"] = 1,
                ["previous"] = head,
                ["payload"] = payload,
                ["signature"] = signature
            };
            lines.Add(entry.ToJsonString());
            head = signature;
        }
        File.WriteAllLines(Path.Combine(directory, "audit.jsonl"), lines);
    }
}
