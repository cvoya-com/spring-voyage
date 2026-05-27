// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Slug;

using System.Text;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Default <see cref="ISlackThreadSlugBuilder"/> implementation. The
/// builder calls <see cref="ITenantUserHumanResolver"/> to pick the
/// Hat-to-drop (ADR-0061 §4 / ADR-0062 §5) and
/// <see cref="IParticipantDisplayNameResolver"/> to resolve every
/// remaining participant's display name. Slugification is the
/// minimal ASCII-safe projection (lowercase, replace non
/// <c>[a-z0-9]</c> with <c>-</c>, collapse runs, trim) so the slug
/// is a clean Slack-message-text token regardless of unicode /
/// punctuation in any participant's display name.
/// </summary>
/// <remarks>
/// <para>
/// Lifetime is <b>singleton</b>: the builder holds no per-request
/// state. Scoped collaborators (<see cref="ITenantUserHumanResolver"/>,
/// <see cref="IParticipantDisplayNameResolver"/>) are resolved through
/// an <see cref="IServiceScopeFactory"/> per call — the same
/// singleton-safety pattern <c>SlackInstallStore</c> uses.
/// </para>
/// <para>
/// Ordering is by canonical wire form (<see cref="Address.ToString"/>,
/// lower-case <c>scheme:&lt;32-hex&gt;</c>) so the slug rule agrees
/// with <c>EfThreadRegistry.Canonicalise</c> on "the same thread."
/// Two different canonicalisations are not introduced.
/// </para>
/// </remarks>
public sealed class SlackThreadSlugBuilder : ISlackThreadSlugBuilder
{
    /// <summary>Prefix on every emitted slug.</summary>
    public const string SlugPrefix = "sv";

    /// <summary>Separator between the prefix and each display-name token.</summary>
    public const char Separator = '-';

    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Creates a new <see cref="SlackThreadSlugBuilder"/>.</summary>
    public SlackThreadSlugBuilder(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<string> BuildSlugAsync(
        IReadOnlyList<Address> participants,
        Guid boundTenantUserId,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participants);
        if (participants.Count == 0)
        {
            throw new ArgumentException(
                "Thread participants must contain at least one address.",
                nameof(participants));
        }

        if (boundTenantUserId == Guid.Empty)
        {
            throw new ArgumentException(
                "Bound TenantUser id must not be Guid.Empty.",
                nameof(boundTenantUserId));
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var hatResolver = sp.GetRequiredService<ITenantUserHumanResolver>();
        var nameResolver = sp.GetRequiredService<IParticipantDisplayNameResolver>();

        // 1. Pick the Hat to drop. ADR-0062 §3 hierarchy:
        //    per-thread reply Hat → PrimaryHumanId → any bound Human.
        //    Passing null for thread skips the reply-pin branch
        //    (new-thread path); the resolver still returns a Hat from
        //    PrimaryHumanId / any-bound. NoBoundHumanException leaks
        //    out — the caller is on a code path where the bound user
        //    has no Humans bound at all, which is a structural error
        //    the slug-builder cannot recover from.
        var hatAddress = await hatResolver
            .PickFromAsync(
                callerTenantUserId: boundTenantUserId,
                explicitFromHumanId: null,
                threadId: threadId == Guid.Empty ? null : threadId,
                cancellationToken)
            .ConfigureAwait(false);

        var hatToDropId = hatAddress.Id;

        // 2. Order participants deterministically by Address.Id (the
        //    actor's stable Guid). Stable across calls for the same
        //    participant set, and set-uniqueness is preserved because
        //    the same set always sorts the same way. The slug is a
        //    display string in a Slack message — it does not name
        //    thread identity (the registry's canonical-wire-form key
        //    does that). Sorting by Guid here keeps the rule simple
        //    and decouples the slug from the registry's ordering
        //    choice.
        var ordered = participants
            .Where(p => p is not null)
            .Select(p => new
            {
                Address = p,
                Canonical = p.ToString(),
            })
            .DistinctBy(x => x.Canonical, StringComparer.Ordinal)
            .OrderBy(x => x.Address.Id)
            .ToList();

        // 3. Drop the Hat (when present) and resolve display names for
        //    the survivors. Per the ADR §4 footnote, only one Human is
        //    ever dropped — every other Human stays in the slug so
        //    distinct threads sharing humans (set-uniqueness invariant)
        //    produce distinct slugs.
        var builder = new StringBuilder(SlugPrefix);
        var droppedOnce = false;
        foreach (var entry in ordered)
        {
            var address = entry.Address;

            // The drop is keyed on (scheme = human, id = Hat). The
            // resolver always returns a human:// Address, so the
            // type match is the common case; the scheme guard stops
            // a stray Guid collision against a non-human participant.
            if (!droppedOnce
                && string.Equals(address.Scheme, Address.HumanScheme, StringComparison.Ordinal)
                && address.Id == hatToDropId)
            {
                droppedOnce = true;
                continue;
            }

            var rawName = await nameResolver
                .ResolveAsync(address.ToString(), cancellationToken)
                .ConfigureAwait(false);

            var slugified = Slugify(rawName);
            if (slugified.Length == 0)
            {
                // Fallback: per IParticipantDisplayNameResolver's
                // non-empty contract this is unreachable, but a
                // genuinely empty token would corrupt the slug. Use
                // the Guid path so the slug stays unambiguous.
                slugified = address.Path;
            }

            builder.Append(Separator);
            builder.Append(slugified);
        }

        return builder.ToString();
    }

    /// <summary>
    /// ASCII-safe lowercase projection of a display name. Lowercases,
    /// keeps <c>[a-z0-9]</c> as-is, replaces every other character
    /// with the separator, and collapses runs so multi-character
    /// punctuation does not balloon the slug.
    /// </summary>
    internal static string Slugify(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(name.Length);
        var lastWasSeparator = true; // suppress leading separators
        foreach (var ch in name)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                buffer.Append(char.ToLowerInvariant(ch));
                lastWasSeparator = false;
            }
            else if (!lastWasSeparator)
            {
                buffer.Append(Separator);
                lastWasSeparator = true;
            }
        }

        // Trim any trailing separator.
        while (buffer.Length > 0 && buffer[^1] == Separator)
        {
            buffer.Length--;
        }

        return buffer.ToString();
    }
}
