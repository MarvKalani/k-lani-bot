using KLaniBot.Startup;

namespace KLaniBot.Audit;

// The operator checks a whole audit journal on the node, every segment in full, for example before archiving old
// segments; start-up reads only the recent ones. The option names the directory that holds audit.jsonl,
// audit.key and the sealed segments (state/actuator for the actuator's journal). The check only reads. On a running
// node the last line can be caught half written and then reads as "not valid"; check a stopped node or repeat it.
internal static class AuditCommands
{
    public static int Verify(NodeArguments arguments)
    {
        string directory = Path.GetFullPath(arguments.RequireOption("--verify-audit"));
        long entries = AuditJournal.VerifyAll(directory);
        Console.WriteLine("Audit journal intact: " + entries + " entries.");
        return 0;
    }
}
