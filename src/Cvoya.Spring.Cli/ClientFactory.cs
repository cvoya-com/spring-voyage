/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

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
