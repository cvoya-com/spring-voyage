// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using System;
using System.Net.Http;
using System.Net.Http.Headers;

/// <summary>
/// Factory for creating configured <see cref="SpringApiClient"/> instances
/// using the current CLI configuration. A single <see cref="HttpClient"/>
/// is shared across all invocations within the process to avoid the
/// socket-exhaustion antipattern.
/// </summary>
public static class ClientFactory
{
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Creates a new <see cref="SpringApiClient"/> configured from <c>~/.spring/config.json</c>.
    /// </summary>
    /// <param name="baseUrlOverride">
    /// Optional base URL that takes precedence over the <c>SPRING_API_URL</c>
    /// environment variable and the CLI config file. Resolution order:
    /// <paramref name="baseUrlOverride"/>, then <c>SPRING_API_URL</c>, then
    /// <c>~/.spring/config.json</c>.
    /// </param>
    public static SpringApiClient Create(string? baseUrlOverride = null)
    {
        var config = CliConfig.Load();

        var baseUrl = baseUrlOverride
            ?? Environment.GetEnvironmentVariable("SPRING_API_URL")
            ?? config.Endpoint;

        if (config.ApiToken is not null)
        {
            SharedHttpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiToken);
        }

        return new SpringApiClient(SharedHttpClient, baseUrl);
    }
}