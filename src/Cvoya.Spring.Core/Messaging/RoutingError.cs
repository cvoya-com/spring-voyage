// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Represents an error that occurred during message routing.
/// </summary>
/// <param name="Code">
/// A top-level discriminator identifying the error class
/// (<c>ADDRESS_NOT_FOUND</c>, <c>PERMISSION_DENIED</c>,
/// <c>DELIVERY_FAILED</c>, <c>CALLER_VALIDATION</c>). Endpoint code
/// switches on this to pick the HTTP status.
/// </param>
/// <param name="Message">
/// A human-readable error description suitable for logs. Callers
/// that need the RFC-7807 <c>detail</c> for a ProblemDetails body
/// should prefer <see cref="Detail"/>, which excludes router-added
/// prefixes (e.g. <c>"Delivery to agent://… failed: …"</c>) so
/// operators see the raw actor-supplied message.
/// </param>
/// <param name="Detail">
/// A human-readable description scoped to the underlying failure
/// (without router framing). Falls back to <see cref="Message"/>
/// when not supplied by the factory.
/// </param>
/// <param name="DetailCode">
/// For <see cref="CallerValidation"/> errors, a stable
/// machine-readable sub-code (e.g. <c>"MISSING_CONVERSATION_ID"</c>)
/// that clients can switch on without parsing <see cref="Detail"/>.
/// Null for all other error classes.
/// </param>
public record RoutingError(
    string Code,
    string Message,
    string? Detail = null,
    string? DetailCode = null)
{
    /// <summary>
    /// Creates an error indicating the target address could not be resolved.
    /// </summary>
    /// <param name="address">The address that was not found.</param>
    /// <returns>A routing error for address not found.</returns>
    public static RoutingError AddressNotFound(Address address)
    {
        var detail = $"No directory entry found for address {address}";
        return new("ADDRESS_NOT_FOUND", detail, detail);
    }

    /// <summary>
    /// Creates an error indicating the sender lacks permission to reach the target.
    /// </summary>
    /// <param name="address">The address that was denied.</param>
    /// <returns>A routing error for permission denied.</returns>
    public static RoutingError PermissionDenied(Address address)
    {
        var detail = $"Permission denied for address {address}";
        return new("PERMISSION_DENIED", detail, detail);
    }

    /// <summary>
    /// Creates an error indicating message delivery to the actor failed.
    /// Reserved for downstream / infrastructure failures; caller-side
    /// validation failures thrown by actors should flow through
    /// <see cref="CallerValidation"/> instead so they surface as 400,
    /// not 502.
    /// </summary>
    /// <param name="address">The target address.</param>
    /// <param name="reason">The reason delivery failed.</param>
    /// <returns>A routing error for delivery failure.</returns>
    public static RoutingError DeliveryFailed(Address address, string reason) =>
        new(
            "DELIVERY_FAILED",
            $"Delivery to {address} failed: {reason}",
            reason);

    /// <summary>
    /// Creates an error indicating the inbound message failed a
    /// caller-side validation rule inside the destination actor
    /// (e.g. required field missing, unknown message type). Maps to
    /// HTTP <c>400 Bad Request</c> at the API boundary, with
    /// <paramref name="detailCode"/> surfaced as an RFC-7807
    /// extension so clients can switch on it without string-matching.
    /// </summary>
    /// <param name="address">The target address.</param>
    /// <param name="detailCode">Stable machine-readable sub-code.</param>
    /// <param name="detail">Human-readable description.</param>
    /// <returns>A routing error for caller-side validation.</returns>
    public static RoutingError CallerValidation(Address address, string detailCode, string detail) =>
        new(
            "CALLER_VALIDATION",
            $"Caller validation failed for {address}: {detail}",
            detail,
            detailCode);
}