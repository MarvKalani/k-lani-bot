namespace KLaniBot.Credentials;

// Execution handlers acquire credentials against current grants; plain values are never cached between operation steps.
public sealed class CredentialBroker(ISecretProvider provider, IReadOnlyDictionary<SecretReference, CredentialGrant> grants,
    TimeProvider? time = null)
{
    private readonly TimeProvider _clock = time ?? TimeProvider.System;

    public bool TryAcquire(SecretReference reference, CredentialUse use, out SecretLease? lease, out CredentialFailure failure)
    {
        lease = null;
        failure = CredentialFailure.Denied;
        try
        {
            if (!TryAuthorize(reference, use, out failure))
            {
                return false;
            }
            if (!provider.TryRead(reference, out lease, out failure) || lease == null)
            {
                lease?.Dispose();
                lease = null;
                failure = failure == CredentialFailure.None ? CredentialFailure.ProviderFailed : failure;
                return false;
            }
            if (!TryAuthorize(reference, use, out failure))
            {
                lease?.Dispose();
                lease = null;
                return false;
            }

            return lease != null;
        }
        catch (Exception)
        {
            lease?.Dispose();
            lease = null;
            failure = CredentialFailure.ProviderFailed;
            return false;
        }
    }

    private bool TryAuthorize(SecretReference reference, CredentialUse use, out CredentialFailure failure)
    {
        failure = CredentialFailure.Denied;
        if (!ValidReference(reference))
        {
            failure = CredentialFailure.InvalidReference;
            return false;
        }
        if (!Enum.IsDefined(use.Purpose) || !grants.TryGetValue(reference, out var grant)
            || grant.Reference != reference || grant.Use != use || grant.State != CredentialState.Active)
        {
            return false;
        }
        if (grant.ExpiresAt <= _clock.GetUtcNow())
        {
            failure = CredentialFailure.Expired;
            return false;
        }
        failure = CredentialFailure.None;

        return true;
    }

    internal static bool ValidReference(SecretReference? reference)
    {
        return reference != null && reference.Id.Version == 7 && reference.Version > 0;
    }
}
