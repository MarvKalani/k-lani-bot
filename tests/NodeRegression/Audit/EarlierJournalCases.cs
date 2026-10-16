using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KLaniBot.Audit;

// An installed node from before journal format 2 is updated in place. Its journal has no sequence numbers and no checkpoint;
// the new node sets it aside unchanged and starts a new chain that names it, instead of refusing to start.
internal static class EarlierJournalCases
{
    private const string EarlierEntries =
        "{\"version\":1,\"previous\":\"\",\"payload\":{\"event\":\"session_connected\"},\"signature\":\"c2lnbmF0dXJl\"}\n";

    public static int Run(string temporaryRoot)
    {
        var checks = new Checks("Earlier journal");
        string stateRoot = Path.Combine(temporaryRoot, "earlier-journal");
        Directory.CreateDirectory(stateRoot);
        File.WriteAllBytes(Path.Combine(stateRoot, "audit.key"), RandomNumberGenerator.GetBytes(32));
        File.WriteAllText(Path.Combine(stateRoot, "audit.jsonl"), EarlierEntries);
        string earlierSha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(stateRoot, "audit.jsonl"))));

        var journal = new AuditJournal(stateRoot);

        string setAside = Path.Combine(stateRoot, "audit-format-1.jsonl");
        checks.That(File.Exists(setAside) && File.ReadAllText(setAside) == EarlierEntries, "the earlier journal is kept unchanged");
        JsonNode first = JsonNode.Parse(File.ReadLines(Path.Combine(stateRoot, "audit.jsonl")).First())!;
        checks.That(first["version"]!.GetValue<int>() == 2 && first["payload"]!["event"]!.ToString() == "earlier_journal_set_aside"
            && first["payload"]!["sha256"]!.ToString() == earlierSha256, "the new chain starts by naming the earlier file and its hash");
        checks.That(journal.Sequence == 1 && new AuditJournal(stateRoot).Sequence == 1, "the new chain reads back after a restart");

        string withCheckpoint = Path.Combine(temporaryRoot, "earlier-journal-with-checkpoint");
        Directory.CreateDirectory(withCheckpoint);
        File.Copy(Path.Combine(stateRoot, "audit.key"), Path.Combine(withCheckpoint, "audit.key"));
        File.Copy(Path.Combine(stateRoot, "audit.checkpoint"), Path.Combine(withCheckpoint, "audit.checkpoint"));
        File.WriteAllText(Path.Combine(withCheckpoint, "audit.jsonl"), EarlierEntries);
        checks.That(Refuses(withCheckpoint) && !File.Exists(Path.Combine(withCheckpoint, "audit-format-1.jsonl")),
            "an old-looking file next to a checkpoint is refused, so a cut cannot pose as an earlier journal");
        return checks.Passed;
    }

    private static bool Refuses(string stateRoot)
    {
        try
        {
            _ = new AuditJournal(stateRoot);
            return false;
        }
        catch (InvalidDataException)
        {
            return true;
        }
    }
}
