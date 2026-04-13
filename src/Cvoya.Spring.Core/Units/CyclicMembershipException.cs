// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Raised when adding a <c>unit://</c> member would introduce a cycle in the
/// unit-membership graph (self-loop, back-edge, or exceeding the maximum
/// traversal depth).
/// <para>
/// The exception carries the <see cref="ParentUnit"/> that was about to accept
/// the new member, the <see cref="CandidateMember"/> being added, and an
/// ordered <see cref="CyclePath"/> describing the sequence of units from the
/// candidate back to the parent (or the point where the depth bound was
/// exceeded). Endpoints surface this as a 409 Conflict ProblemDetails per the
/// platform error-shape convention.
/// </para>
/// </summary>
public class CyclicMembershipException : SpringException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CyclicMembershipException"/> class.
    /// </summary>
    /// <param name="parentUnit">The unit that was about to accept the member.</param>
    /// <param name="candidateMember">The <c>unit://</c> member being added.</param>
    /// <param name="cyclePath">The sequence of units from candidate back to parent (inclusive on both ends when a true cycle is detected; truncated at the depth bound otherwise).</param>
    /// <param name="message">The human-readable error message.</param>
    public CyclicMembershipException(
        Address parentUnit,
        Address candidateMember,
        IReadOnlyList<Address> cyclePath,
        string message)
        : base(message)
    {
        ParentUnit = parentUnit;
        CandidateMember = candidateMember;
        CyclePath = cyclePath;
    }

    /// <summary>
    /// Gets the unit that was about to accept the new member.
    /// </summary>
    public Address ParentUnit { get; }

    /// <summary>
    /// Gets the <c>unit://</c> member address whose addition was rejected.
    /// </summary>
    public Address CandidateMember { get; }

    /// <summary>
    /// Gets the sequence of units that form the detected cycle (or the path
    /// walked before the depth bound was reached). Includes
    /// <see cref="CandidateMember"/> as the first element. For a true cycle
    /// that closes on <see cref="ParentUnit"/>, the parent is the last
    /// element. For a depth-bound rejection, the last element is the deepest
    /// unit reached.
    /// </summary>
    public IReadOnlyList<Address> CyclePath { get; }
}