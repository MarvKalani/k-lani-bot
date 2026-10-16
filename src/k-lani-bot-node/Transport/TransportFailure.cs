using System.Diagnostics;

namespace KLaniBot.Transport;

// Why a controller could not complete a call to a node. It sits next to the unchanged outcome, which
// alone decides whether a mutation may have run; this value only tells the operator where to look.
public enum TransportFailure
{
    // The credential file or environment variable named in the registry cannot be read.
    CredentialUnavailable,

    // The credential has the wrong form, or the node answered 401 to it.
    CredentialInvalid,

    // The node presented a certificate that does not chain to the CA pinned in the registry.
    CaPinMismatch,

    // The certificate chains to the pinned CA but does not fit the address, or TLS failed otherwise.
    TlsUntrusted,

    // The call did not finish within its deadline.
    Timeout,

    // The caller withdrew the call.
    Cancelled,

    // No connection could be opened, or it broke before the answer was complete.
    ConnectionFailed,

    // The node answered with an error status other than 401.
    NodeRejected,

    // The answer exceeded the bounded result frame.
    ResponseTooLarge,

    // The node does not speak the expected HTTP version or answered with something that is not a result.
    ProtocolUnavailable
}

// Transport failures are reported as words; this table gives each failure its word.
public static class TransportFailureNames
{
    public static string ToWireName(this TransportFailure failure)
    {
        return failure switch
        {
            TransportFailure.CredentialUnavailable => "credential_unavailable",
            TransportFailure.CredentialInvalid => "credential_invalid",
            TransportFailure.CaPinMismatch => "ca_pin_mismatch",
            TransportFailure.TlsUntrusted => "tls_untrusted",
            TransportFailure.Timeout => "timeout",
            TransportFailure.Cancelled => "cancelled",
            TransportFailure.ConnectionFailed => "connection_failed",
            TransportFailure.NodeRejected => "node_rejected",
            TransportFailure.ResponseTooLarge => "response_too_large",
            TransportFailure.ProtocolUnavailable => "protocol_unavailable",
            _ => throw new UnreachableException()
        };
    }
}

// Thrown where the client itself recognises the cause, so the classification does not have to guess
// it back from a framework exception.
public sealed class TransportFailureException(TransportFailure failure, string message) : Exception(message)
{
    public TransportFailure Failure
    {
        get;
    } = failure;
}
