// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Routing;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Represents an error that occurred during message routing.
/// </summary>
/// <param name="Code">A machine-readable error code.</param>
/// <param name="Message">A human-readable error description.</param>
public record RoutingError(string Code, string Message)
{
    /// <summary>
    /// Creates an error indicating the target address could not be resolved.
    /// </summary>
    /// <param name="address">The address that was not found.</param>
    /// <returns>A routing error for address not found.</returns>
    public static RoutingError AddressNotFound(Address address) =>
        new("ADDRESS_NOT_FOUND", $"No directory entry found for address {address.Scheme}://{address.Path}");

    /// <summary>
    /// Creates an error indicating the sender lacks permission to reach the target.
    /// </summary>
    /// <param name="address">The address that was denied.</param>
    /// <returns>A routing error for permission denied.</returns>
    public static RoutingError PermissionDenied(Address address) =>
        new("PERMISSION_DENIED", $"Permission denied for address {address.Scheme}://{address.Path}");

    /// <summary>
    /// Creates an error indicating message delivery to the actor failed.
    /// </summary>
    /// <param name="address">The target address.</param>
    /// <param name="reason">The reason delivery failed.</param>
    /// <returns>A routing error for delivery failure.</returns>
    public static RoutingError DeliveryFailed(Address address, string reason) =>
        new("DELIVERY_FAILED", $"Delivery to {address.Scheme}://{address.Path} failed: {reason}");
}
