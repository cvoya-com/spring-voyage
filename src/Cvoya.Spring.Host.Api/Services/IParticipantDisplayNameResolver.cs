// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

/// <summary>
/// Resolves a human-readable display name for an address string of the form
/// <c>scheme://path</c>. Supported schemes are <c>agent</c>, <c>unit</c>,
/// and <c>human</c>. For <c>agent://</c> the name comes from
/// <see cref="Cvoya.Spring.Core.Execution.AgentDefinition.Name"/>; for
/// <c>unit://</c> from the <c>UnitDefinitionEntity.Name</c>; for
/// <c>human://</c> from the <c>ApiTokenEntity</c> display-name claim (same
/// source as <c>UserProfileResponse.DisplayName</c>).
///
/// Caches results for the duration of a single request scope so repeated
/// calls for the same address (e.g. the same agent appearing in multiple
/// inbox rows) issue at most one database query.
///
/// When no definition can be found (deleted agent, missing human profile, or
/// an unknown scheme) the resolver falls back to the address <em>path</em>
/// component — that is, the same readable slug the portal previously derived
/// client-side — and logs at <c>Debug</c>.
/// </summary>
public interface IParticipantDisplayNameResolver
{
    /// <summary>
    /// Returns the display name for <paramref name="address"/>, or the
    /// path component of the address when resolution fails.
    /// </summary>
    /// <param name="address">
    /// A <c>scheme://path</c> address string, e.g. <c>agent://ada</c>.
    /// </param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    ValueTask<string> ResolveAsync(string address, CancellationToken cancellationToken = default);
}