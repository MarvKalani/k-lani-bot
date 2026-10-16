using KLaniBot.Backup;
using KLaniBot.Credentials;
using KLaniBot.Guard;

namespace KLaniBot.Startup;

// The operator runs --create-backup-key --policy policy.json once per node and key generation. The node id of the policy
// selects the vault scope that the backup tools of the same node read later.
internal static class BackupKeyCommands
{
    // Throws InvalidDataException when the policy cannot be read.
    public static int Create(NodeArguments arguments)
    {
        string policyPath = Path.GetFullPath(arguments.RequireOption("--policy"));
        NodePolicy policy = NodePolicy.Read(policyPath).Match(valid => valid,
            problem => throw new InvalidDataException(problem.Describe()));
        ISecretProvider? vault = NodeBackup.PlatformKeys(policy);

        if (vault == null)
        {
            Console.Error.WriteLine("This operating system has no qualified key vault for backups; no key was created.");
            return 1;
        }
        var created = BackupKeyProvisioning.TryCreate(vault, out CredentialFailure failure);
        if (created == null)
        {
            Console.Error.WriteLine("The vault refused the backup key (" + failure + "); no key was created.");
            return 1;
        }
        Console.WriteLine("Backup key created in the vault. Add these two fields to the backup section of the policy:");
        Console.WriteLine(created.ToJsonString());
        return 0;
    }
}
