// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Raised when a composition operation would leave a non-top-level unit
/// with zero parent-unit edges, violating the "every unit has a parent"
/// invariant (review feedback on #744). Also raised when a unit is created
/// without a parent and without the explicit <c>IsTopLevel</c> flag.
/// <para>
/// Mirrors <see cref="AgentMembershipRequiredException"/> for the
/// unit-parent case. Carries the <see cref="UnitAddress"/> whose last
/// parent-unit edge was about to be removed (or the fresh unit name on
/// create) and the <see cref="ParentUnitId"/> the operation targeted,
/// when known. Endpoints surface this as a 409 Conflict ProblemDetails
/// per the platform error-shape convention established by
/// <see cref="CyclicMembershipException"/>.
/// </para>
/// <para>
/// Top-level units carry the explicit <c>IsTopLevel=true</c> marker and
/// have no parent-unit edges by design — this check only fires for
/// non-top-level units.
/// </para>
/// </summary>
public class UnitParentRequiredException : SpringException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UnitParentRequiredException"/> class
    /// for last-parent-removal attempts.
    /// </summary>
    /// <param name="unitAddress">The canonical unit-address path whose last parent-unit removal was rejected.</param>
    /// <param name="parentUnitId">The parent the operation targeted, when known. <c>null</c> on create-time rejections.</param>
    /// <param name="message">The human-readable error message.</param>
    public UnitParentRequiredException(
        string unitAddress,
        string? parentUnitId,
        string message)
        : base(message)
    {
        UnitAddress = unitAddress;
        ParentUnitId = parentUnitId;
    }

    /// <summary>
    /// Gets the unit-address path (equivalent to <c>new Address("unit", id).Path</c>)
    /// whose last parent-unit removal was rejected, or the create-time unit name
    /// that lacked any parent.
    /// </summary>
    public string UnitAddress { get; }

    /// <summary>
    /// Gets the parent the operation targeted, when known. <c>null</c> when the
    /// exception originates on the create path (no specific parent to report).
    /// </summary>
    public string? ParentUnitId { get; }
}