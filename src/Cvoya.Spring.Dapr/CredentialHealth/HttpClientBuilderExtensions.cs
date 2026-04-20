// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.CredentialHealth;

using Cvoya.Spring.Core.CredentialHealth;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// DI-facing extension that attaches
/// <see cref="CredentialHealthWatchdogHandler"/> to a named
/// <see cref="HttpClient"/>.
/// </summary>
public static class HttpClientBuilderExtensions
{
    /// <summary>
    /// Registers a <see cref="CredentialHealthWatchdogHandler"/> on the
    /// supplied <see cref="IHttpClientBuilder"/> so every response the
    /// client receives flows through the watchdog's auth-status check.
    /// </summary>
    /// <param name="builder">The HTTP-client builder to extend.</param>
    /// <param name="kind">Whether the subject is a runtime or connector.</param>
    /// <param name="subjectId">Runtime id or connector slug this client talks to.</param>
    /// <param name="secretName">
    /// Secret name within the subject. Convention: <c>"api-key"</c> for
    /// single-credential subjects; connectors with multi-part auth pick
    /// a stable name per credential (e.g. <c>"app-id"</c>,
    /// <c>"private-key"</c>).
    /// </param>
    /// <returns>The same <see cref="IHttpClientBuilder"/> for chaining.</returns>
    public static IHttpClientBuilder AddCredentialHealthWatchdog(
        this IHttpClientBuilder builder,
        CredentialHealthKind kind,
        string subjectId,
        string secretName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);

        return builder.AddHttpMessageHandler(sp => new CredentialHealthWatchdogHandler(
            sp.GetRequiredService<IServiceScopeFactory>(),
            kind,
            subjectId,
            secretName,
            sp.GetRequiredService<ILogger<CredentialHealthWatchdogHandler>>()));
    }
}