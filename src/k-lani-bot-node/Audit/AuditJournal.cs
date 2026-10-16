using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using KLaniBot.Actions;
using KLaniBot.Common;

namespace KLaniBot.Audit;

// Use case: the node records every admitted operation and what it knew of the outcome, so the operator can prove
// afterwards what an agent did and a retry after a restart finds its operation. Entries form an HMAC chain with sequence
// numbers and a signed checkpoint, kept in sealed segments. This exposes changes by anyone without the audit key, not by
// a local administrator who holds it; that would need an anchor outside this machine.
public sealed class AuditJournal
{
    private const int CurrentVersion = 2;
    private const int CheckpointVersion = 1;
    private const string ActiveFileName = "audit.jsonl";
    private const string SealEvent = "audit_segment_sealed";
    public const long DefaultSegmentLimitBytes = 16 * 1024 * 1024;

    // One line of the journal: its sequence number, the signature of the previous line, the payload and its own signature.
    private sealed record JournalEntry(long Sequence, string Previous, JsonObject Payload, string Signature);

    // The signed sequence number and head signature the journal had last; a journal that ends before it was shortened.
    private sealed record Checkpoint(long Sequence, string Head);

    // A journal of an earlier format that was set aside unchanged, and the SHA-256 the new chain records for it.
    private sealed record SetAsideJournal(string FileName, string Sha256);

    private readonly string _directory;
    private readonly string _path;
    private readonly string _checkpointPath;
    private readonly byte[] _key;
    private readonly TimeSpan _window;
    private readonly TimeProvider _clock;
    private readonly long _segmentLimitBytes;
    // Set by VerifyAll: every segment is read in full and nothing is written, not even a checkpoint catch-up.
    private readonly bool _verificationOnly;
    private readonly object _appendLock = new();
    private string _head = "";
    private long _sequence;
    private bool _faulted;

    // Recent operations by "principal:request_id", rebuilt from the journal at startup.
    public OperationLedger Operations
    {
        get;
    }

    public long Sequence => _sequence;

    // Throws InvalidDataException when the journal, a segment or the checkpoint shows a change, a gap or a cut end.
    public AuditJournal(string stateRoot, TimeSpan? operationWindow = null, TimeProvider? clock = null)
        : this(stateRoot, operationWindow, clock, DefaultSegmentLimitBytes, verificationOnly: false)
    {
    }

    internal AuditJournal(
        string stateRoot,
        TimeSpan? operationWindow,
        TimeProvider? clock,
        long segmentLimitBytes,
        bool verificationOnly)
    {
        _window = operationWindow ?? OperationLedger.DefaultWindow;
        _clock = clock ?? TimeProvider.System;
        _segmentLimitBytes = segmentLimitBytes;
        _verificationOnly = verificationOnly;
        Operations = new OperationLedger(_window, clock);
        _directory = stateRoot;
        _path = Path.Combine(stateRoot, ActiveFileName);
        _checkpointPath = Path.Combine(stateRoot, "audit.checkpoint");
        _key = LoadOrCreateKey(Path.Combine(stateRoot, "audit.key"));
        SetAsideJournal? setAside = verificationOnly ? null : SetAsideEarlierFormat();
        Checkpoint? checkpoint = ReadCheckpoint();
        ReplayJournal(checkpoint, out string? signatureAtCheckpoint);
        RequireIntactAgainstCheckpoint(checkpoint, signatureAtCheckpoint);
        if (setAside != null)
        {
            Append(new JsonObject
            {
                ["event"] = "earlier_journal_set_aside",
                ["file"] = setAside.FileName,
                ["sha256"] = setAside.Sha256,
                ["timestamp"] = _clock.GetUtcNow().ToString("O")
            });
        }
    }

    // A journal written before format 2 has no sequence numbers and no checkpoint, and this node does not read it. It is moved
    // aside unchanged, so an installed node can be updated, and the new chain starts by naming it. A journal with a checkpoint
    // or sealed segments is format 2 history, where an unreadable entry stays a refusal, so a cut cannot pose as an old file.
    // Throws InvalidDataException when an earlier journal was set aside before and would be overwritten.
    private SetAsideJournal? SetAsideEarlierFormat()
    {
        bool withoutFormatTwoHistory = File.Exists(_path) && !File.Exists(_checkpointPath) && SealedSegments().Count == 0;
        int? version = withoutFormatTwoHistory ? FirstEntryVersion(_path) : null;
        if (version == null || version >= CurrentVersion)
        {
            return null;
        }

        string fileName = "audit-format-" + version.Value.ToString(CultureInfo.InvariantCulture) + ".jsonl";
        string destination = Path.Combine(_directory, fileName);
        if (File.Exists(destination))
        {
            throw new InvalidDataException("An earlier journal was already set aside as " + fileName + "; nothing is overwritten.");
        }
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(_path)));
        File.Move(_path, destination);
        return new SetAsideJournal(fileName, sha256);
    }

    // The format version of the first entry, or null when the file starts with no readable entry.
    private static int? FirstEntryVersion(string path)
    {
        string? firstLine = File.ReadLines(path).FirstOrDefault(line => line.Length > 0);
        try
        {
            return firstLine == null ? null : JsonNode.Parse(firstLine)?["version"]?.GetValue<int>();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    // Reads every segment in full and writes nothing; returns the number of entries.
    // Throws InvalidDataException like the constructor.
    public static long VerifyAll(string stateRoot)
    {
        if (!File.Exists(Path.Combine(stateRoot, "audit.key")))
        {
            throw new InvalidDataException("No audit key in this directory; nothing to verify.");
        }
        var journal = new AuditJournal(stateRoot, null, null, DefaultSegmentLimitBytes, verificationOnly: true);
        return journal.Sequence;
    }

    // Throws InvalidOperationException after an earlier failure, and rethrows the Exception of a failed write after marking the journal
    // failed.
    public void Append(JsonObject payload)
    {
        lock (_appendLock)
        {
            if (_faulted)
            {
                throw new InvalidOperationException("Audit journal failed earlier; restart the node to verify it before continuing.");
            }
            try
            {
                AppendEntry(payload);
            }
            catch
            {
                // A failed or partial write leaves the file in an unknown state. Writing further
                // entries after it would hide that, so refuse until the next start verifies the file.
                _faulted = true;
                throw;
            }
        }
    }

    private void AppendEntry(JsonObject payload)
    {
        if (ActiveSegmentIsFull())
        {
            SealActiveSegment();
        }
        WriteEntry(payload);
    }

    private bool ActiveSegmentIsFull()
    {
        return File.Exists(_path) && new FileInfo(_path).Length >= _segmentLimitBytes;
    }

    // A crash between the seal entry and the rename only adds a second seal at the next append.
    // Segments are never deleted here; archiving them is the operator's decision.
    private void SealActiveSegment()
    {
        int number = SealedSegments().Count + 1;
        WriteEntry(new JsonObject
        {
            ["event"] = SealEvent,
            ["segment"] = number,
            ["timestamp"] = _clock.GetUtcNow().ToString("O")
        });
        File.Move(_path, Path.Combine(_directory, SegmentFileName(number)));
    }

    private static string SegmentFileName(int number)
    {
        return "audit-" + number.ToString("D6", CultureInfo.InvariantCulture) + ".jsonl";
    }

    // The sealed segments in order. Their numbers must run from 1 without a gap: a missing file is a missing
    // part of the history.
    // Throws InvalidDataException when a segment number is missing.
    private List<string> SealedSegments()
    {
        var numbered = new SortedDictionary<int, string>();
        foreach (string file in Directory.EnumerateFiles(_directory, "audit-*.jsonl"))
        {
            string digits = Path.GetFileNameWithoutExtension(file)["audit-".Length..];
            if (digits.Length == 6 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int number))
            {
                numbered[number] = file;
            }
        }
        int expected = 1;
        foreach (int number in numbered.Keys)
        {
            if (number != expected)
            {
                throw new InvalidDataException("Audit segment " + expected + " is missing; the history is incomplete.");
            }
            expected++;
        }
        return numbered.Values.ToList();
    }

    private void WriteEntry(JsonObject payload)
    {
        long sequence = _sequence + 1;
        string signature = Sign(_head, sequence, payload);
        var entry = new JsonObject
        {
            ["version"] = CurrentVersion,
            ["seq"] = sequence,
            ["previous"] = _head,
            ["payload"] = payload.DeepClone(),
            ["signature"] = signature
        };
        using (var stream = PrivateFile.OpenWrite(_path, FileMode.Append, FileShare.Read))
        {
            stream.Write(Encoding.UTF8.GetBytes(entry.ToJsonString() + "\n"));
            stream.Flush(true);
        }
        _head = signature;
        _sequence = sequence;
        WriteCheckpoint();
    }

    // Throws InvalidDataException when the key is missing although history exists, or is malformed.
    private byte[] LoadOrCreateKey(string keyPath)
    {
        if (!File.Exists(keyPath))
        {
            if (File.Exists(_path) || SealedSegments().Count > 0)
            {
                throw new InvalidDataException("Audit key missing; history is not discarded.");
            }
            PrivateFile.WriteAllBytes(keyPath, RandomNumberGenerator.GetBytes(32));
        }
        byte[] key = File.ReadAllBytes(keyPath);
        if (key.Length != 32)
        {
            throw new InvalidDataException("Invalid audit key.");
        }
        return key;
    }

    // Sealed segments inside the operation window are read in full, because they can hold operations a retry must find.
    private void ReplayJournal(Checkpoint? checkpoint, out string? signatureAtCheckpoint)
    {
        signatureAtCheckpoint = null;
        List<string> sealedSegments = SealedSegments();
        int firstFullyRead = _verificationOnly ? 0 : FirstSegmentInsideWindow(sealedSegments);
        for (int index = 0; index < sealedSegments.Count; index++)
        {
            if (index < firstFullyRead)
            {
                CheckSegmentBoundaries(sealedSegments[index], checkpoint, ref signatureAtCheckpoint);
            }
            else
            {
                ReplayFile(sealedSegments[index], checkpoint, ref signatureAtCheckpoint);
            }
        }
        if (File.Exists(_path))
        {
            ReplayFile(_path, checkpoint, ref signatureAtCheckpoint);
        }
    }

    private void ReplayFile(string path, Checkpoint? checkpoint, ref string? signatureAtCheckpoint)
    {
        string name = Path.GetFileName(path);
        long lineNumber = 0;
        foreach (string line in File.ReadLines(path))
        {
            lineNumber++;
            JournalEntry entry = ParseEntry(line, name, lineNumber);
            VerifyEntry(entry, name, lineNumber);
            Advance(entry, checkpoint, ref signatureAtCheckpoint);
            RememberOperation(entry.Payload);
        }
    }

    // A segment outside the window: its first entry must continue the chain, its last entry must be authentic,
    // and the chain goes on from there. The entries between are left to VerifyAll.
    // Throws InvalidDataException when the segment does not continue the chain or its last entry is not authentic.
    private void CheckSegmentBoundaries(string path, Checkpoint? checkpoint, ref string? signatureAtCheckpoint)
    {
        string name = Path.GetFileName(path);
        JournalEntry first = ParseEntry(ReadFirstLine(path), name, 1);
        VerifyEntry(first, name, 1);
        Advance(first, checkpoint, ref signatureAtCheckpoint);
        JournalEntry last = ParseEntry(ReadLastLine(path), name, 0);
        bool lastAuthentic = SignaturesMatch(Sign(last.Previous, last.Sequence, last.Payload), last.Signature);
        if (!lastAuthentic || last.Sequence < first.Sequence)
        {
            throw new InvalidDataException("Audit history integrity failed at the end of " + name + ".");
        }
        if (last.Sequence > first.Sequence)
        {
            _head = last.Signature;
            _sequence = last.Sequence;
            CaptureCheckpoint(last, checkpoint, ref signatureAtCheckpoint);
        }
    }

    private void Advance(JournalEntry entry, Checkpoint? checkpoint, ref string? signatureAtCheckpoint)
    {
        _head = entry.Signature;
        _sequence = entry.Sequence;
        CaptureCheckpoint(entry, checkpoint, ref signatureAtCheckpoint);
    }

    private static void CaptureCheckpoint(JournalEntry entry, Checkpoint? checkpoint, ref string? signatureAtCheckpoint)
    {
        if (checkpoint != null && entry.Sequence == checkpoint.Sequence)
        {
            signatureAtCheckpoint = entry.Signature;
        }
    }

    // The first segment sealed before the window (plus the tolerance for clocks running ahead) and every older one cannot
    // hold an operation a retry asks about. The signed seal time decides, not the file time, which anyone can change.
    private int FirstSegmentInsideWindow(List<string> sealedSegments)
    {
        DateTimeOffset threshold = _clock.GetUtcNow() - _window - OperationLedger.FutureTolerance;
        for (int index = sealedSegments.Count - 1; index >= 0; index--)
        {
            string name = Path.GetFileName(sealedSegments[index]);
            JournalEntry last = ParseEntry(ReadLastLine(sealedSegments[index]), name, 0);
            bool authentic = SignaturesMatch(Sign(last.Previous, last.Sequence, last.Payload), last.Signature);
            bool sealedBeforeWindow = authentic
                && last.Payload["event"]?.ToString() == SealEvent
                && DateTimeOffset.TryParse(
                    last.Payload["timestamp"]?.ToString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTimeOffset sealedAt)
                && sealedAt < threshold;
            if (sealedBeforeWindow)
            {
                return index + 1;
            }
        }
        return 0;
    }

    // Throws InvalidDataException for an empty segment.
    private static string ReadFirstLine(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        return reader.ReadLine() ?? throw new InvalidDataException("Audit segment " + Path.GetFileName(path) + " is empty.");
    }

    // Reads only the end of the file. An entry longer than the tail that is read falls back to reading the file.
    // Throws InvalidDataException for an empty segment.
    private static string ReadLastLine(string path)
    {
        const int TailBytes = 1024 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long start = Math.Max(0, stream.Length - TailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        byte[] tail = new byte[stream.Length - start];
        stream.ReadExactly(tail);
        string text = Encoding.UTF8.GetString(tail).TrimEnd('\n');
        int lineStart = text.LastIndexOf('\n');
        if (lineStart >= 0)
        {
            return text[(lineStart + 1)..];
        }
        if (start == 0 && text.Length > 0)
        {
            return text;
        }
        return File.ReadLines(path).LastOrDefault(line => line.Length > 0)
            ?? throw new InvalidDataException("Audit segment " + Path.GetFileName(path) + " is empty.");
    }

    // A line number of 0 means the last line of the file, read without counting.
    // Throws InvalidDataException for an entry of unknown version or invalid JSON.
    private static JournalEntry ParseEntry(string line, string file, long lineNumber)
    {
        try
        {
            var node = JsonNode.Parse(line)!.AsObject();
            int version = node["version"]!.GetValue<int>();
            if (version != CurrentVersion)
            {
                throw new InvalidDataException("Audit entry " + Where(file, lineNumber) + " has an unknown version.");
            }
            return new JournalEntry(
                node["seq"]!.GetValue<long>(),
                node["previous"]!.GetValue<string>(),
                node["payload"]!.AsObject(),
                node["signature"]!.GetValue<string>());
        }
        catch (Exception exception) when (
            exception is JsonException or NullReferenceException or InvalidOperationException or FormatException)
        {
            // The line itself is not logged: it may hold decision text.
            throw new InvalidDataException("Audit entry " + Where(file, lineNumber) + " is not valid.", exception);
        }
    }

    private static string Where(string file, long lineNumber)
    {
        return lineNumber == 0 ? "at the end of " + file : lineNumber + " of " + file;
    }

    // Throws InvalidDataException when the entry does not continue the chain or is not authentic.
    private void VerifyEntry(JournalEntry entry, string file, long lineNumber)
    {
        string expectedSignature = Sign(entry.Previous, entry.Sequence, entry.Payload);
        bool chainIntact = entry.Previous == _head && entry.Sequence == _sequence + 1;
        if (!chainIntact || !SignaturesMatch(expectedSignature, entry.Signature))
        {
            throw new InvalidDataException("Audit history integrity failed at entry " + Where(file, lineNumber) + ".");
        }
    }

    private void RememberOperation(JsonObject payload)
    {
        if (payload["operation_key"] is not { } key)
        {
            return;
        }
        ActionOutcome outcome = ActionOutcomeNames.Read(payload);
        // An admitted operation without a recorded result may have reached the operating system.
        ActionOutcome recoveredOutcome = outcome == ActionOutcome.Admitted ? ActionOutcome.Unknown : outcome;
        bool succeeded = outcome.ReportsSuccess();
        string requestHash = payload["request_hash"]?.ToString() ?? "";
        Operations.Restore(key.ToString(), requestHash, new JsonObject
        {
            ["success"] = succeeded,
            ["outcome"] = recoveredOutcome.ToWireName(),
            ["receipt"] = payload.DeepClone(),
            ["recovered"] = true
        });
    }

    // A journal cut at the end still forms a valid chain; only the checkpoint shows that entries are missing.
    // Restoring an older journal together with its older checkpoint is not detected.
    // Throws InvalidDataException when entries exist without a checkpoint or the journal ends before it.
    private void RequireIntactAgainstCheckpoint(Checkpoint? checkpoint, string? signatureAtCheckpoint)
    {
        if (checkpoint == null)
        {
            if (_sequence > 0)
            {
                throw new InvalidDataException(
                    "Audit checkpoint missing although the journal has entries; the history may have been shortened.");
            }
            return;
        }
        if (_sequence < checkpoint.Sequence || signatureAtCheckpoint != checkpoint.Head)
        {
            throw new InvalidDataException("Audit history was shortened or replaced; the journal ends before its checkpoint.");
        }
        if (_sequence > checkpoint.Sequence && !_verificationOnly)
        {
            // The node stopped between writing an entry and writing the checkpoint. The chain verified, so catch up.
            WriteCheckpoint();
        }
    }

    // Throws InvalidDataException when the checkpoint is not authentic or not valid.
    private Checkpoint? ReadCheckpoint()
    {
        if (!File.Exists(_checkpointPath))
        {
            return null;
        }
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(_checkpointPath))!.AsObject();
            long sequence = node["seq"]!.GetValue<long>();
            string head = node["head"]!.GetValue<string>();
            bool validVersion = node["version"]!.GetValue<int>() == CheckpointVersion;
            if (!validVersion || !SignaturesMatch(SignCheckpoint(sequence, head), node["mac"]!.GetValue<string>()))
            {
                throw new InvalidDataException("Audit checkpoint is not authentic.");
            }
            return new Checkpoint(sequence, head);
        }
        catch (Exception exception) when (
            exception is JsonException or NullReferenceException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Audit checkpoint is not valid.", exception);
        }
    }

    private void WriteCheckpoint()
    {
        var checkpoint = new JsonObject
        {
            ["version"] = CheckpointVersion,
            ["seq"] = _sequence,
            ["head"] = _head,
            ["mac"] = SignCheckpoint(_sequence, _head)
        };
        PrivateFile.WriteAllBytesAtomically(_checkpointPath, Encoding.UTF8.GetBytes(checkpoint.ToJsonString()));
    }

    // Over the previous signature, the sequence number and the payload, so changing, inserting or reordering an entry
    // breaks the chain.
    private string Sign(string previous, long sequence, JsonObject payload)
    {
        string signedText = previous + "\n" + sequence + "\n" + payload.ToJsonString();
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(signedText)));
    }

    private string SignCheckpoint(long sequence, string head)
    {
        string signedText = "checkpoint\n" + sequence + "\n" + head;
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(signedText)));
    }

    private static bool SignaturesMatch(string expected, string actual)
    {
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual));
    }
}
