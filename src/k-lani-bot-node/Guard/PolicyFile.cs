using System.Text.Json;
using KLaniBot.Cartridges;
using KLaniBot.Fleet;

namespace KLaniBot.Guard;

// Master administration changes one setting of a running node and writes the whole policy back atomically, so the file the
// node enforces always matches the policy in memory.
public static class PolicyFile
{
    // Throws IOException or UnauthorizedAccessException when the file cannot be replaced, and the link check's exception
    // when the policy path is a link.
    public static void Write(NodePolicy policy, string policyPath)
    {
        var format = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        string content = JsonSerializer.Serialize(policy, format);

        PairingProtocol.WritePrivate(policyPath + ".new", content, true);
        CartridgeManifest.RejectLinks(policyPath);
        File.Move(policyPath + ".new", policyPath, true);
    }
}

// A master administrator switches the backup requirement of a node; the change is written to the policy at once, and a
// failed write leaves the policy as it was.
public static class BackupRequirementSetting
{
    // Rethrows the Exception of a failed write after restoring the previous value.
    public static void Set(NodePolicy policy, string policyPath, BackupRequirement requirement)
    {
        BackupRequirement previous = policy.BackupRequirement;
        try
        {
            policy.BackupRequirement = requirement;
            PolicyFile.Write(policy, policyPath);
        }
        catch
        {
            policy.BackupRequirement = previous;
            throw;
        }
    }
}
