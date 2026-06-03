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
///     The caller's bound Hat that is a canonical participant of the
///     supplied thread (reply default — ADR-0062 § 5, generalised by
///     #2865). Catches both received-as (the inbound named the Hat as
///     recipient) and originated-as (the caller started the thread as
///     the Hat); both make the Hat a participant, which is the identity
///     invariant ADR-0030 names. Multi-Hat threads tie-break to the
///     most recent received-as Hat, then the most recent originated-as,
///     then the lowest-Guid eligible participant.
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
    /// Stable error code returned (with HTTP 403) when the caller wears no
    /// Hat that can reach the message's target(s) — the Hat ↔ unit
    /// reachability gate (#2972). The send is rejected because there is no
    /// human member, under any unit the caller is a member of, that lets
    /// them address the target.
    /// </summary>
    public const string NoReachableHatCode = "NoReachableHat";

    /// <summary>
    /// Stable error code returned (with HTTP 403) when the caller supplied
    /// an explicit "speaking-as" Hat that <em>is</em> bound to them but
    /// cannot reach the message's target(s) under the reachability rule
    /// (#2972). Distinct from <see cref="NoReachableHatCode"/>: the caller
    /// may have another wearable Hat, but the one they chose is wrong for
    /// this target.
    /// </summary>
    public const string HatCannotReachTargetCode = "HatCannotReachTarget";

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
    /// resolver returns the caller's bound Hat that is a canonical
    /// participant of the thread (ADR-0062 § 5 generalised — both
    /// received-as and originated-as resolve here). Falls through to
    /// the next branch when the thread row is unknown or none of the
    /// caller's bound Hats participates.
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

    /// <summary>
    /// Reachability-constrained overload (#2972). Identical to
    /// <see cref="PickFromAsync(Guid, Guid?, Guid?, CancellationToken)"/>
    /// except every resolution branch (explicit override, thread
    /// participant, primary, fallback) is restricted to
    /// <paramref name="allowedHumanIds"/> — the Hats that can reach the
    /// message's target(s), computed by the message-send endpoint via
    /// <see cref="Cvoya.Spring.Core.Units.IHatReachabilityService"/>. The
    /// stamped sender is therefore always a Hat the caller may address the
    /// target with. An explicit override that is bound but excluded throws
    /// <see cref="HatNotReachableException"/>. Passing <c>null</c> is
    /// equivalent to the unconstrained overload.
    /// </summary>
    /// <param name="callerTenantUserId">The authenticated caller's <c>TenantUser</c> id.</param>
    /// <param name="explicitFromHumanId">Optional explicit "speaking-as" Hat override.</param>
    /// <param name="threadId">Optional thread for reply-default resolution.</param>
    /// <param name="allowedHumanIds">
    /// The reachability-constrained candidate set. When non-null the resolved
    /// Hat MUST be one of these ids.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<Address> PickFromAsync(
        Guid callerTenantUserId,
        Guid? explicitFromHumanId,
        Guid? threadId,
        IReadOnlyCollection<Guid>? allowedHumanIds,
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

/// <summary>
/// Thrown by <see cref="ITenantUserHumanResolver.PickFromAsync"/> when the
/// caller supplied an explicit "speaking-as" Hat that is bound to them but
/// is not in the reachability-constrained <c>allowedHumanIds</c> set — i.e.
/// the chosen Hat cannot address the message's target(s) (#2972). The API
/// layer translates this into a 403 ProblemDetails with the
/// <see cref="ITenantUserHumanResolver.HatCannotReachTargetCode"/> extension
/// code.
/// </summary>
public sealed class HatNotReachableException : Exception
{
    /// <inheritdoc />
    public HatNotReachableException(string message)
        : base(message)
    {
    }
}
