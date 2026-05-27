// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Slug;

using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Default <see cref="ISlackThreadSlugBuilder"/> implementation. The
/// builder calls <see cref="ITenantUserHumanResolver"/> to pick the
/// Hat-to-drop (ADR-0061 §4 / ADR-0062 §5) and the participant
/// display-name resolver to render every remaining participant's
/// label. Slugification is the minimal ASCII-safe projection so the
/// slug is a clean Slack-message-text token regardless of unicode /
/// punctuation in any participant's display name.
/// </summary>
/// <remarks>
/// <para>
/// Lifetime is <b>singleton</b>: the builder holds no per-request
/// state. Scoped collaborators are resolved through an
/// <see cref="IServiceScopeFactory"/> per call — the same
/// singleton-safety pattern <c>SlackInstallStore</c> uses.
/// </para>
/// <para>
/// The slug-rule body lands in the next commit; this skeleton wires
/// the DI surface and the constructor shape so the registration
/// test in <c>SlackConnectorRegistrationTests</c> compiles against
/// the new public type.
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
    public Task<string> BuildSlugAsync(
        IReadOnlyList<Address> participants,
        Guid boundTenantUserId,
        Guid threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participants);
        // Slug-rule implementation lands in the next commit.
        throw new NotImplementedException(
            "SlackThreadSlugBuilder.BuildSlugAsync — implementation lands in the slug-rule commit.");
    }
}
