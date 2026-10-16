namespace KLaniBot.Credentials;

// Stored procedures carry this opaque identity instead of a password, token or backup encryption key.
public sealed record SecretReference(Guid Id, int Version);

// The trusted node handler states the use of a credential before the broker checks its operator-supplied grant.
public enum CredentialPurpose
{
    ApplicationLogin,
    BackupEncryption
}

// Revoked metadata remains distinguishable from an unknown reference even if provider cleanup is delayed.
public enum CredentialState
{
    Active,
    Revoked
}

// Caller identity and target come from the authenticated node request and selected application, never package assertions.
public sealed record CredentialUse(string Node, string Principal, string Application, CredentialPurpose Purpose);

// An administrator's metadata grants one exact reference to one purpose and target until its expiry.
public sealed record CredentialGrant(SecretReference Reference, CredentialUse Use, DateTimeOffset ExpiresAt, CredentialState State);

// Expected provider and access failures do not carry secret-bearing native error messages into logs or MCP replies.
public enum CredentialFailure
{
    None,
    InvalidReference,
    InvalidValue,
    Denied,
    Expired,
    NotFound,
    Unavailable,
    ProviderFailed,
    Cancelled
}

// Trusted provisioning and execution use a provider in the node; no raw-secret read endpoint is exposed through MCP.
public interface ISecretProvider
{
    bool TryWrite(SecretReference reference, ReadOnlySpan<byte> value, out CredentialFailure failure);
    bool TryRead(SecretReference reference, out SecretLease? lease, out CredentialFailure failure);
    bool TryDelete(SecretReference reference, out CredentialFailure failure);
}
