// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core;

/// <summary>
/// Marker interface for an agent in the Spring Voyage platform. The
/// Dapr-actor-layer equivalent is <c>Cvoya.Spring.Dapr.Actors.IAgent</c>,
/// which adds the mailbox <c>ReceiveAsync</c> method on top of Dapr's
/// <c>IActor</c> so a unit and an individual agent can be addressed
/// through the same shape.
/// </summary>
public interface IAgent
{
}