namespace KLaniBot.Guard;

// A master administrator can switch a principal's inventory scope; the change is written to policy.json at once, and a failed write leaves
// the policy as it was.
public static class InventoryPermission
{
    // Throws UnauthorizedAccessException for an unknown principal, and rethrows the Exception of a failed write after restoring the
    // principals.
    public static void Set(NodePolicy policy, string policyPath, string principalId, bool enabled)
    {
        PrincipalPolicy[] updated = policy.Principals.Select(principal => principal.Id == principalId
            ? principal with
            {
                Scopes = enabled
                    ? principal.Scopes.Append(Scope.InventoryRead).Distinct().ToArray()
                    : principal.Scopes.Where(scope => scope != Scope.InventoryRead).ToArray()
            }
            : principal).ToArray();
        if (!updated.Any(principal => principal.Id == principalId))
        {
            throw new UnauthorizedAccessException("Unknown inventory principal.");
        }
        PrincipalPolicy[] previous = policy.Principals;
        try
        {
            policy.Principals = updated;
            PolicyFile.Write(policy, policyPath);
        }
        catch
        {
            // Undo for every failure type: the policy in memory must match the file that is enforced.
            policy.Principals = previous;
            throw;
        }
    }
}
