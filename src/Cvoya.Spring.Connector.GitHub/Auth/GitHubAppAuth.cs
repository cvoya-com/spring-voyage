// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Handles GitHub App authentication including JWT generation and
/// installation token management.
/// </summary>
public class GitHubAppAuth(GitHubConnectorOptions options, ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<GitHubAppAuth>();

    /// <summary>
    /// Generates a JSON Web Token (JWT) signed with the GitHub App's private key.
    /// The JWT is valid for 10 minutes, as required by the GitHub API.
    /// </summary>
    /// <returns>A signed JWT string.</returns>
    public string GenerateJwt()
    {
        var now = DateTimeOffset.UtcNow;

        using var rsa = RSA.Create();
        rsa.ImportFromPem(options.PrivateKeyPem);

        var credentials = new SigningCredentials(
            new RsaSecurityKey(rsa),
            SecurityAlgorithms.RsaSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = options.AppId.ToString(),
            IssuedAt = now.UtcDateTime.AddSeconds(-60),
            Expires = now.UtcDateTime.AddMinutes(10),
            SigningCredentials = credentials
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);

        _logger.LogDebug("Generated JWT for GitHub App {AppId}", options.AppId);

        return handler.WriteToken(token);
    }

    /// <summary>
    /// Obtains an installation access token from the GitHub API by exchanging
    /// the App JWT for an installation-scoped token.
    /// </summary>
    /// <param name="installationId">The GitHub App installation ID.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The installation access token.</returns>
    public async Task<string> GetInstallationTokenAsync(
        long installationId,
        CancellationToken cancellationToken = default)
    {
        var jwt = GenerateJwt();

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {jwt}");
        httpClient.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        httpClient.DefaultRequestHeaders.Add("User-Agent", "SpringVoyage-GitHubConnector");

        var response = await httpClient.PostAsync(
            $"https://api.github.com/app/installations/{installationId}/access_tokens",
            null,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var doc = JsonDocument.Parse(json);
        var token = doc.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("GitHub API returned a null token.");

        _logger.LogInformation(
            "Obtained installation token for installation {InstallationId}",
            installationId);

        return token;
    }
}
