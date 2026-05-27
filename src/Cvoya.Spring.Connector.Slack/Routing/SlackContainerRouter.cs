// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Routing;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Default <see cref="ISlackContainerRouter"/>. Counts the bound
/// humans in the participant set and emits one of the three
/// <see cref="SlackContainerRoute"/> branches.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bound human</b> = a participant whose scheme is
/// <see cref="Address.HumanScheme"/> whose id resolves to a
/// <c>TenantUserId</c> appearing in the bound-user list. Per ADR-0062
/// §1 every <c>Human</c> row carries a <c>TenantUserId</c> FK; in OSS
/// every human resolves to the single operator. The connector does
/// not look up that FK here — the resolution happens at the
/// per-message path through <see cref="ITenantUserHumanResolver"/>.
/// The router operates on a pre-resolved list: callers map the
/// thread's <c>human://</c> participants to their <c>TenantUserId</c>
/// via the resolver before invoking the router.
/// </para>
/// <para>
/// For v0.1 OSS the router's "bound human" detection can use the
/// simpler shape: the bound-user list contains the (slack-user-id,
/// tenant-user-id) tuple; any human participant whose tenant-user-id
/// matches one of those tuples is a bound human. This is the shape the
/// router receives because the caller pre-resolves humans to their
/// tenant-user-id via <see cref="ITenantUserHumanResolver"/>.
/// </para>
/// <para>
/// Singleton-safe — no state, no DI dependencies.
/// </para>
/// </remarks>
public sealed class SlackContainerRouter : ISlackContainerRouter
{
    /// <inheritdoc />
    public SlackContainerRoute Route(
        IReadOnlyList<Address> participants,
        IReadOnlyList<TenantBoundUser> boundUsers)
    {
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(boundUsers);

        if (boundUsers.Count == 0)
        {
            // No bound users at all → no Slack-side surface.
            return SlackContainerRoute.None.Instance;
        }

        // ADR-0061 §7.1: iterate the bound-user list — length 1 in OSS,
        // length N in cloud. The "is this participant a bound human?"
        // check matches a human:// participant against any (slack-user,
        // tenant-user) tuple by tenant-user id. The caller pre-resolves
        // human:// addresses to their bound TenantUserId so the match
        // here is a simple id comparison.
        var boundHits = new List<TenantBoundUser>();
        foreach (var bound in boundUsers)
        {
            foreach (var participant in participants)
            {
                if (participant is null)
                {
                    continue;
                }

                // The participant's id is the Hat id (Human row id); the
                // bound-user tuple stores the TenantUser id. They are
                // *different* identities — the caller must collapse
                // human participants to their TenantUserId via
                // ITenantUserHumanResolver before passing them in. To
                // keep the router pure and avoid the cycle (the resolver
                // is scoped), the public surface accepts an already-
                // resolved participant set on the Slack-routing side:
                // see ISlackContainerRouter overload usage in
                // SlackOutboundDispatcher.
                if (!string.Equals(participant.Scheme, Address.HumanScheme, StringComparison.Ordinal))
                {
                    continue;
                }

                if (participant.Id == bound.TenantUserId)
                {
                    boundHits.Add(bound);
                    break;
                }
            }
        }

        // Deduplicate hits — the same bound user only counts once even
        // if the participant set somehow listed them twice.
        var distinctHits = boundHits
            .DistinctBy(b => b.TenantUserId)
            .ToList();

        return distinctHits.Count switch
        {
            0 => SlackContainerRoute.None.Instance,
            1 => new SlackContainerRoute.DirectMessage(distinctHits[0].ExternalUserId),
            _ => new SlackContainerRoute.PrivateChannel(string.Empty),
        };
    }
}
