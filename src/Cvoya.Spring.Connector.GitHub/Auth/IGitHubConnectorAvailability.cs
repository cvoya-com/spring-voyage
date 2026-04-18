// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

/// <summary>
/// Reports whether the GitHub connector is fully configured at startup.
/// Consumed by connector-scoped endpoints (for example
/// <c>/api/v1/connectors/github/actions/list-installations</c>) so they can
/// short-circuit with a structured "disabled" response instead of attempting
/// a JWT sign that is guaranteed to fail.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton by
/// <see cref="DependencyInjection.ServiceCollectionExtensions.AddCvoyaSpringConnectorGitHub"/>.
/// The registration is driven by
/// <see cref="GitHubAppCredentialsValidator.Classify"/>, so malformed
/// credentials fail at connector-init and never surface as a runtime 502
/// (#609).
/// </para>
/// <para>
/// The private cloud repo can substitute a tenant-scoped implementation
/// (e.g. "App installed for tenant X, missing for tenant Y") by registering
/// its own singleton before <c>AddCvoyaSpringConnectorGitHub</c> runs —
/// <c>TryAdd*</c> guards the default registration.
/// </para>
/// </remarks>
public interface IGitHubConnectorAvailability
{
    /// <summary>
    /// <c>true</c> when the connector has usable App credentials and the
    /// hot path can run. <c>false</c> when both credentials were missing
    /// at startup and the connector registered itself in a disabled state.
    /// </summary>
    bool IsEnabled { get; }

    /// <summary>
    /// When <see cref="IsEnabled"/> is <c>false</c>, a short human-readable
    /// explanation that endpoints surface to callers (portal + CLI).
    /// <c>null</c> when the connector is enabled.
    /// </summary>
    string? DisabledReason { get; }
}

/// <summary>
/// Immutable default implementation of <see cref="IGitHubConnectorAvailability"/>
/// baked at service-registration time. Connector options don't change after
/// startup, so there is no value in re-classifying at resolve time.
/// </summary>
/// <param name="IsEnabled">Whether the connector is enabled.</param>
/// <param name="DisabledReason">
/// Short reason surfaced to portal/CLI when <paramref name="IsEnabled"/>
/// is <c>false</c>. Must be <c>null</c> when enabled.
/// </param>
public sealed record GitHubConnectorAvailability(
    bool IsEnabled,
    string? DisabledReason) : IGitHubConnectorAvailability
{
    /// <summary>
    /// Singleton used in wiring when App credentials parse cleanly at startup.
    /// </summary>
    public static GitHubConnectorAvailability Enabled { get; } =
        new(IsEnabled: true, DisabledReason: null);

    /// <summary>
    /// Builds a disabled-state singleton carrying the supplied reason.
    /// </summary>
    /// <param name="reason">The reason surfaced to operators.</param>
    /// <returns>A disabled <see cref="GitHubConnectorAvailability"/>.</returns>
    public static GitHubConnectorAvailability Disabled(string reason) =>
        new(IsEnabled: false, DisabledReason: reason);
}