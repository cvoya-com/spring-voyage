// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Resolves the routable <c>human://</c> sender address for an outbound
/// message constructed at an API boundary (ADR-0062 § 3). The auth
/// principal is a <c>tenant-user://</c> address; the wire-level
/// <see cref="Message.From"/> must be one of the routable kinds
/// (<c>agent</c> / <c>unit</c> / <c>human</c>) so every downstream
/// consumer — routing, the directory, the agent-facing tool surface, the
/// portal render path — can treat the field uniformly. The rewrite from
/// auth principal to "speaking-as" Hat is the only seam that needs the
/// caller's bound-Human set; this resolver concentrates it on one DI
/// surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolution order</b> (ADR-0062 § 3):
/// <list type="number">
///   <item><description>
///     An explicit <paramref name="explicitFromHumanId"/> supplied by the
///     caller, after validating that the named Human is one of the
///     caller's bound Humans. An unbound Human is a 400 with
///     <see cref="NoBoundHumanCode"/>.
///   </description></item>
///   <item><description>
///     The Hat pinned by the thread on its inbound side (reply default).
///     The thread-pinned default is not surfaced through this method's
///     signature; callers that have a thread context pass it via the
///     thread-aware overload (TBD; v0.1 reply-pin is captured on the
///     thread row).
///   </description></item>
///   <item><description>
///     The caller's <c>TenantUser.PrimaryHumanId</c> (ADR-0062 § 2).
///   </description></item>
///   <item><description>
///     A 400 <see cref="NoBoundHumanCode"/> if the caller has no bound
///     Humans. Unreachable in the OSS deployment (the default resolver
///     always seeds one) but the correct error for a cloud
///     <c>TenantUser</c> not yet bound to any Human.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <b>Why an interface in Core.</b> Pinning the contract in
/// <c>Cvoya.Spring.Core.Messaging</c> alongside <see cref="Address"/> and
/// <see cref="Message"/> means downstream callers — and the cloud-overlay
/// implementation — depend only on the abstraction, not on
/// <c>Cvoya.Spring.Dapr</c>. The OSS implementation lives in
/// <c>Cvoya.Spring.Dapr.Messaging</c> and reads the FK on
/// <c>humans.tenant_user_id</c> through <c>SpringDbContext</c>.
/// </para>
/// </remarks>
public interface ITenantUserHumanResolver
{
    /// <summary>
    /// Stable error code returned to the API surface when the caller has
    /// no bound Human and the API endpoint must fail with a structured
    /// 400. ADR-0062 § 3 names this as the "<c>NoBoundHuman</c>" branch.
    /// Surfaced verbatim on the response so clients can switch on it
    /// without parsing the human-readable message.
    /// </summary>
    public const string NoBoundHumanCode = "NoBoundHuman";

    /// <summary>
    /// Resolves the routable <c>human://</c> sender Address for an
    /// outbound message. Throws <see cref="NoBoundHumanException"/> when
    /// no Hat is available (the explicit override is invalid or the
    /// caller has no bound Humans).
    /// </summary>
    /// <param name="callerTenantUserId">
    /// The authenticated caller's <c>TenantUser</c> id (the
    /// <c>tenant-user://&lt;id&gt;</c> identity post-auth). Never
    /// <see cref="System.Guid.Empty"/>.
    /// </param>
    /// <param name="explicitFromHumanId">
    /// Optional explicit override naming the Hat to stamp on
    /// <see cref="Message.From"/>. When supplied, validated as a member
    /// of the caller's bound set; an unbound Human surfaces as
    /// <see cref="NoBoundHumanException"/>.
    /// </param>
    /// <param name="threadId">
    /// Optional thread the outbound message is being sent on. When
    /// supplied and <paramref name="explicitFromHumanId"/> is null, the
    /// resolver inspects the thread's recent inbound messages and pins
    /// the Hat that received the inbound — the reply default
    /// (ADR-0062 § 5).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// The routable <see cref="Address"/> (<c>human://&lt;id&gt;</c>) to
    /// stamp on <see cref="Message.From"/>.
    /// </returns>
    Task<Address> PickFromAsync(
        Guid callerTenantUserId,
        Guid? explicitFromHumanId,
        Guid? threadId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Thrown by <see cref="ITenantUserHumanResolver.PickFromAsync"/> when the
/// caller has no resolvable Human sender (the explicit override is invalid
/// or the caller has no bound Humans). The API layer translates this into
/// a 400 ProblemDetails with the
/// <see cref="ITenantUserHumanResolver.NoBoundHumanCode"/> extension code.
/// </summary>
public sealed class NoBoundHumanException : Exception
{
    /// <inheritdoc />
    public NoBoundHumanException(string message)
        : base(message)
    {
    }
}
