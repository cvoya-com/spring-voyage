// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Raised when a membership operation would leave an agent with zero
/// unit memberships, violating the "every agent belongs to at least one
/// unit" invariant (#744). Also raised when an agent is created without
/// any unit membership.
/// <para>
/// Carries the <see cref="AgentAddress"/> whose last membership was
/// about to be removed (or the fresh agent name on create) and the
/// <see cref="UnitId"/> the operation targeted, when known. Endpoints
/// surface this as a 409 Conflict ProblemDetails per the platform
/// error-shape convention established by <see cref="CyclicMembershipException"/>.
/// </para>
/// </summary>
public class AgentMembershipRequiredException : SpringException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AgentMembershipRequiredException"/> class
    /// for last-membership-removal attempts.
    /// </summary>
    /// <param name="agentAddress">The canonical agent-address path whose last membership removal was rejected.</param>
    /// <param name="unitId">The unit the operation targeted, when known. <c>null</c> on create-time rejections.</param>
    /// <param name="message">The human-readable error message.</param>
    public AgentMembershipRequiredException(
        string agentAddress,
        string? unitId,
        string message)
        : base(message)
    {
        AgentAddress = agentAddress;
        UnitId = unitId;
    }

    /// <summary>
    /// Gets the agent-address path (equivalent to <c>new Address("agent", id).Path</c>)
    /// whose last membership removal was rejected, or the create-time agent name
    /// that lacked any unit membership.
    /// </summary>
    public string AgentAddress { get; }

    /// <summary>
    /// Gets the unit the operation targeted, when known. <c>null</c> when the
    /// exception originates on the create path (no specific unit to report).
    /// </summary>
    public string? UnitId { get; }
}