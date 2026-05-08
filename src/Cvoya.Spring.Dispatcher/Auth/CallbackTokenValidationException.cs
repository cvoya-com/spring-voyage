// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Auth;

using Cvoya.Spring.Core;

/// <summary>
/// Raised by <see cref="CallbackTokenValidator"/> when an inbound callback
/// token fails integrity validation — bad signature, expired, malformed
/// JWT, or missing/invalid required claims.
/// </summary>
/// <remarks>
/// The validator never inspects the directory; this exception covers only
/// token-shape and cryptographic-integrity failures. Authorisation gates
/// (caller-is-unit, target-is-direct-child, self-delegation, depth budget)
/// raise their own typed errors from the endpoint handler (D13).
/// </remarks>
public class CallbackTokenValidationException : SpringException
{
    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="CallbackTokenValidationException"/> class with the supplied
    /// reason code and a human-readable message.
    /// </summary>
    /// <param name="reason">
    /// Machine-readable reason — one of
    /// <see cref="CallbackTokenValidationReason"/>.
    /// </param>
    /// <param name="message">Human-readable message.</param>
    public CallbackTokenValidationException(CallbackTokenValidationReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>
    /// Initializes a new instance of the
    /// <see cref="CallbackTokenValidationException"/> class with the supplied
    /// reason code, message, and underlying exception.
    /// </summary>
    /// <param name="reason">
    /// Machine-readable reason — one of
    /// <see cref="CallbackTokenValidationReason"/>.
    /// </param>
    /// <param name="message">Human-readable message.</param>
    /// <param name="innerException">The underlying failure, if any.</param>
    public CallbackTokenValidationException(
        CallbackTokenValidationReason reason,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Reason = reason;
    }

    /// <summary>
    /// Machine-readable failure reason. Endpoint handlers map this onto a
    /// concrete HTTP status code (every reason in this enum maps to 401).
    /// </summary>
    public CallbackTokenValidationReason Reason { get; }
}

/// <summary>
/// Closed enumeration of validator failure modes. Every value maps to HTTP
/// 401 in the endpoint handler — the token is structurally invalid in some
/// way.
/// </summary>
public enum CallbackTokenValidationReason
{
    /// <summary>The token string is malformed (not a valid JWT).</summary>
    Malformed,

    /// <summary>The token's signature does not verify under any tenant key the validator could resolve.</summary>
    SignatureInvalid,

    /// <summary>The token expired before this call.</summary>
    Expired,

    /// <summary>One of the required callback claims is missing or has the wrong shape.</summary>
    ClaimMissingOrInvalid,
}