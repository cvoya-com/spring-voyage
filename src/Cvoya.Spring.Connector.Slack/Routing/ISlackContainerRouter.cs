// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Routing;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Resolves an SV thread's participant set + the tenant's bound-user
/// list to a Slack-side container per
/// <see href="https://github.com/cvoya-com/spring-voyage/blob/main/docs/decisions/0061-slack-connector-oss-shape.md">ADR-0061</see>
/// §7.2 / §7.8. Pure function; no I/O.
///
/// <para>
/// <b>Why a function and not a property of the thread</b>: ADR-0061
/// §7.8 forbids "container == DM" hardcodes. Every Slack-outbound
/// path reads through this router so the OSS DM-only / hybrid private-
/// channel split lands as a single new branch here, not as a refactor
/// of every caller.
/// </para>
/// </summary>
public interface ISlackContainerRouter
{
    /// <summary>
    /// Routes <paramref name="participants"/> against
    /// <paramref name="boundUsers"/>. The result is one of the three
    /// <see cref="SlackContainerRoute"/> branches:
    /// </summary>
    /// <list type="bullet">
    ///   <item><description>
    ///     Exactly one bound human in the participant set →
    ///     <see cref="SlackContainerRoute.DirectMessage"/>.
    ///   </description></item>
    ///   <item><description>
    ///     Two or more bound humans →
    ///     <see cref="SlackContainerRoute.PrivateChannel"/>. The
    ///     channel id is the empty string in v0.1 (the branch is
    ///     reachable for forward-compat seam preservation but
    ///     consumers throw <see cref="NotSupportedException"/>).
    ///   </description></item>
    ///   <item><description>
    ///     Zero bound humans → <see cref="SlackContainerRoute.None"/>.
    ///   </description></item>
    /// </list>
    /// <param name="participants">
    /// Every participant of the SV thread (humans, agents, units).
    /// Order is irrelevant. Must not be <c>null</c>.
    /// </param>
    /// <param name="boundUsers">
    /// The Slack binding's bound-user list from
    /// <see cref="ITenantConnectorBindingStore.GetBoundUsersAsync"/>.
    /// ADR-0061 §7.1: length 1 in OSS, length N in cloud — the
    /// implementation iterates regardless.
    /// </param>
    /// <returns>The resolved container, never <c>null</c>.</returns>
    SlackContainerRoute Route(
        IReadOnlyList<Address> participants,
        IReadOnlyList<TenantBoundUser> boundUsers);
}
