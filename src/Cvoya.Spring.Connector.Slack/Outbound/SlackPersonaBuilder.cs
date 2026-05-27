// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Outbound;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Security;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Default <see cref="ISlackPersonaBuilder"/>. Uses
/// <see cref="IParticipantDisplayNameResolver"/> for display names and
/// a deterministic placeholder URL for icons. The icon URL is intended
/// to surface "this is an SV-generated message" visually without
/// requiring a live avatar service in v0.1 — the placeholder service
/// (<see cref="PlaceholderIconBaseUrl"/>) renders per-id avatars from
/// the address's Guid.
/// </summary>
/// <remarks>
/// <para>
/// Singleton — stateless; scoped collaborators are resolved per call
/// through <see cref="IServiceScopeFactory"/>, matching the pattern
/// used by <see cref="Slack.Slug.SlackThreadSlugBuilder"/> and
/// <see cref="Slack.Auth.OAuth.SlackInstallStore"/>.
/// </para>
/// </remarks>
public sealed class SlackPersonaBuilder : ISlackPersonaBuilder
{
    /// <summary>
    /// Base URL of the placeholder icon service. Emits a deterministic
    /// avatar per Guid; surface chosen so the OSS image cost is zero
    /// (the service is a public stateless renderer). Air-gapped
    /// deployments need an overridable base URL — see #2842.
    /// </summary>
    public const string PlaceholderIconBaseUrl = "https://www.gravatar.com/avatar/";

    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Creates a new <see cref="SlackPersonaBuilder"/>.</summary>
    public SlackPersonaBuilder(IServiceScopeFactory scopeFactory)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<SlackPersona> ResolveAsync(
        Address participant,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(participant);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IParticipantDisplayNameResolver>();

        var displayName = await resolver
            .ResolveAsync(participant.ToString(), cancellationToken)
            .ConfigureAwait(false);

        return new SlackPersona(
            Username: displayName,
            IconUrl: BuildIconUrl(participant));
    }

    /// <summary>
    /// Builds the icon URL for a participant address. Uses Gravatar's
    /// identicon mode with the address's Guid as the hash key —
    /// deterministic, no external state, OSS-safe.
    /// </summary>
    internal static string BuildIconUrl(Address participant)
    {
        // Gravatar identicons key off the MD5 hash of the seed. Since
        // we don't carry an email for SV agents/units we use the
        // address's canonical hex as the key. Gravatar treats unknown
        // hashes as identicons (?d=identicon), which is what we want.
        var seed = GuidFormatter.Format(participant.Id);
        return $"{PlaceholderIconBaseUrl}{seed}?d=identicon&s=64";
    }
}
