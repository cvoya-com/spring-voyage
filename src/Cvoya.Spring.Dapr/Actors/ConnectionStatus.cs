// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Represents the connection status of a connector actor.
/// </summary>
public enum ConnectionStatus
{
    /// <summary>
    /// The connector is not connected to any external system.
    /// </summary>
    Disconnected,

    /// <summary>
    /// The connector is actively connected and operational.
    /// </summary>
    Connected,

    /// <summary>
    /// The connector encountered an error and is in a failed state.
    /// </summary>
    Error
}