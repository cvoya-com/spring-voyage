// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Protocol-agnostic seam that skill callers use to invoke a resolved skill
/// (#359). Replaces the temptation to reach into <see cref="IMessageRouter"/>
/// directly from the skill surface — the default OSS implementation routes
/// through the internal bus, but a future A2A gateway implementation (#539)
/// will slot in here as an alternative without touching callers.
/// </summary>
/// <remarks>
/// <para>
/// The contract is intentionally minimal:
/// </para>
/// <list type="bullet">
///   <item><description>Takes a <see cref="SkillInvocation"/> — a name, a JSON
///     payload, and caller/correlation context.</description></item>
///   <item><description>Returns a <see cref="SkillInvocationResult"/> — success
///     with payload or machine-readable failure code.</description></item>
///   <item><description>Does <em>not</em> leak <see cref="Message"/> onto the
///     caller. The default implementation builds a
///     <see cref="MessageType.Domain"/> message and dispatches via
///     <see cref="IMessageRouter"/> internally.</description></item>
/// </list>
/// <para>
/// Implementations MUST preserve the governance chain: boundary opacity /
/// projection (#413 / #497), permission cascades (#414), initiative levels
/// (#415), cloning policy (#416), activity emission (#391 / #484), and the
/// unit-policy enforcer (#162). The OSS default achieves this for free by
/// routing through <see cref="IMessageRouter"/>, which is the single
/// enforcement seam for all internal messaging. Alternative implementations
/// (A2A gateway, test doubles) must either delegate to the router or
/// re-implement every check.
/// </para>
/// </remarks>
public interface ISkillInvoker
{
    /// <summary>
    /// Invokes a skill by name with the supplied arguments and returns the
    /// result. Implementations resolve the skill name against an
    /// <see cref="ISkillRegistry"/> or an expertise-directory-driven catalog,
    /// apply governance checks, and dispatch.
    /// </summary>
    /// <param name="invocation">The skill invocation record.</param>
    /// <param name="cancellationToken">A token to cancel the call.</param>
    /// <returns>The skill invocation result.</returns>
    Task<SkillInvocationResult> InvokeAsync(
        SkillInvocation invocation,
        CancellationToken cancellationToken = default);
}
