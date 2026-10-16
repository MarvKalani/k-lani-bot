using System.Security.Cryptography;
using System.Text.Json.Nodes;
using KLaniBot.Credentials;

namespace KLaniBot.Backup;

// Use case: the operator, at the node, creates the vault key that encrypts this node's backups and copies only its
// identifier into the backup section of policy.json. The key bytes go straight into the vault and are never printed,
// written to a file or offered through MCP.
internal static class BackupKeyProvisioning
{
    private const int KeyBytes = 32;

    // The answer is the JSON the operator pastes into the backup section; null when the vault refused the key.
    public static JsonObject? TryCreate(ISecretProvider vault, out CredentialFailure failure)
    {
        var reference = new SecretReference(Guid.CreateVersion7(), 1);
        byte[] key = RandomNumberGenerator.GetBytes(KeyBytes);

        try
        {
            if (!vault.TryWrite(reference, key, out failure))
            {
                return null;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
        return new JsonObject
        {
            ["keyId"] = reference.Id.ToString(),
            ["keyVersion"] = reference.Version
        };
    }
}
