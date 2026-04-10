// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using System.Net.Http.Headers;

/// <summary>
/// Factory for creating configured <see cref="SpringApiClient"/> instances
/// using the current CLI configuration.
/// </summary>
public static class ClientFactory
{
    /// <summary>
    /// Creates a new <see cref="SpringApiClient"/> configured from ~/.spring/config.json.
    /// </summary>
    public static SpringApiClient Create()
    {
        var config = CliConfig.Load();
        var httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.Endpoint)
        };

        if (config.ApiToken is not null)
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiToken);
        }

        return new SpringApiClient(httpClient);
    }
}
