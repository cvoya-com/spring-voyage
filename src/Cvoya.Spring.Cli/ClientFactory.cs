// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using System.Net.Http;
using System.Net.Http.Headers;

/// <summary>
/// Factory for creating configured <see cref="SpringApiClient"/> instances
/// using the current CLI configuration.
/// </summary>
public static class ClientFactory
{
    /// <summary>
    /// Creates a new <see cref="SpringApiClient"/> configured from <c>~/.spring/config.json</c>.
    /// </summary>
    public static SpringApiClient Create()
    {
        var config = CliConfig.Load();
        var httpClient = new HttpClient();

        if (config.ApiToken is not null)
        {
            httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", config.ApiToken);
        }

        return new SpringApiClient(httpClient, config.Endpoint);
    }
}