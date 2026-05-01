// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Raised when a membership operation would leave an agent with zero
/// unit memberships, violating the "every agent belongs to at least one
/// unit" invariant (#744). Also raised when an agent is created without
/// any unit membership.
/// <para>
/// Carries the <see cref="AgentId"/> whose last membership was about to
/// be removed (or the fresh agent UUID on create) and the
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
    /// <param name="agentId">The stable UUID of the agent whose last membership removal was rejected.</param>
    /// <param name="unitId">The UUID of the unit the operation targeted, when known. <c>null</c> on create-time rejections.</param>
    /// <param name="message">The human-readable error message.</param>
    public AgentMembershipRequiredException(
        Guid agentId,
        Guid? unitId,
        string message)
        : base(message)
    {
        AgentId = agentId;
        UnitId = unitId;
    }

    /// <summary>
    /// Gets the stable UUID of the agent whose last membership removal was rejected,
    /// or the create-time agent UUID that lacked any unit membership.
    /// </summary>
    public Guid AgentId { get; }

    /// <summary>
    /// Gets the unit UUID the operation targeted, when known. <c>null</c> when the
    /// exception originates on the create path (no specific unit to report).
    /// </summary>
    public Guid? UnitId { get; }
}