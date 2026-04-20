// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Raised when a composition or membership operation would cross a
/// tenant boundary — e.g. adding an agent from tenant A as a member of
/// a unit in tenant B, or attaching a sub-unit from a different tenant
/// than the parent (#745).
/// <para>
/// Carries the <see cref="ParentUnit"/> the operation targeted and the
/// <see cref="CandidateMember"/> that failed the same-tenant check.
/// Endpoints surface this as a 404 Not Found ProblemDetails — we do
/// not leak the existence of entities in other tenants by returning
/// 403/409, matching the tenant-isolation pattern used elsewhere in
/// the platform (e.g. the skill-bundle binding decorator raises a
/// generic "not found" rather than "not bound to this tenant").
/// </para>
/// </summary>
public class CrossTenantMembershipException : SpringException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CrossTenantMembershipException"/> class.
    /// </summary>
    /// <param name="parentUnit">The unit that was about to accept the member.</param>
    /// <param name="candidateMember">The <c>agent://</c> or <c>unit://</c> address whose tenant differed from <paramref name="parentUnit"/>.</param>
    /// <param name="message">The human-readable error message.</param>
    public CrossTenantMembershipException(
        Address parentUnit,
        Address candidateMember,
        string message)
        : base(message)
    {
        ParentUnit = parentUnit;
        CandidateMember = candidateMember;
    }

    /// <summary>Gets the unit that was about to accept the new member.</summary>
    public Address ParentUnit { get; }

    /// <summary>Gets the address whose tenant differed from <see cref="ParentUnit"/>.</summary>
    public Address CandidateMember { get; }
}