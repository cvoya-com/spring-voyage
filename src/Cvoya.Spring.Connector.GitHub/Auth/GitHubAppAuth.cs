// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth;

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Handles GitHub App authentication including JWT generation and
/// installation-token minting. Minting is exposed raw so the layer above
/// (<see cref="IInstallationTokenCache"/>) owns the caching / proactive-refresh
/// policy — this class is a thin transport over GitHub's
/// <c>POST /app/installations/{id}/access_tokens</c> endpoint.
/// </summary>
public class GitHubAppAuth : IDisposable
{
    /// <summary>
    /// Name of the <see cref="HttpClient"/> this class resolves through
    /// <see cref="IHttpClientFactory"/> for App-auth token minting. Exposed
    /// as a constant so the host can attach the credential-health watchdog
    /// (see <c>CONVENTIONS.md</c> § 16) to the same logical client.
    /// </summary>
    public const string HttpClientName = "github-app";

    private readonly GitHubConnectorOptions _options;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    // Hold a single RSA instance for the lifetime of this singleton so the
    // SignatureProvider that Microsoft.IdentityModel.Tokens caches inside
    // CryptoProviderFactory.Default keeps a reference to a live key. Disposing
    // the RSA between calls (the previous behaviour) left the cached
    // SignatureProvider holding a disposed RSA, which threw
    // ObjectDisposedException on every call after the first (#1130).
    private readonly Lazy<RSA> _rsa;
    private int _disposed;

    /// <summary>
    /// Initializes the auth helper with connector options, a logger factory,
    /// an optional <see cref="IHttpClientFactory"/> (routes the mint call
    /// through the factory so the host-registered watchdog / proxy handlers
    /// run on the response), and an optional <see cref="TimeProvider"/> used
    /// only for JWT timestamp stability in tests (defaults to
    /// <see cref="TimeProvider.System"/>).
    /// </summary>
    /// <remarks>
    /// <paramref name="httpClientFactory"/> is optional so existing unit
    /// tests that instantiate this class directly (see
    /// <c>Cvoya.Spring.Connector.GitHub.Tests</c>) keep compiling without
    /// wiring up <c>AddHttpClient</c>. Production callers go through DI
    /// which always supplies the factory, so the watchdog is always
    /// attached on the happy path.
    /// </remarks>
    public GitHubAppAuth(
        GitHubConnectorOptions options,
        ILoggerFactory loggerFactory,
        IHttpClientFactory? httpClientFactory = null,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<GitHubAppAuth>();
        _rsa = new Lazy<RSA>(CreateRsa, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private RSA CreateRsa()
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(_options.PrivateKeyPem);
        return rsa;
    }

    /// <summary>
    /// Generates a JSON Web Token (JWT) signed with the GitHub App's private key.
    /// The JWT is valid for 10 minutes, as required by the GitHub API.
    /// </summary>
    /// <returns>A signed JWT string.</returns>
    public string GenerateJwt()
    {
        var now = _timeProvider.GetUtcNow();

        var credentials = new SigningCredentials(
            new RsaSecurityKey(_rsa.Value),
            SecurityAlgorithms.RsaSha256);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.AppId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IssuedAt = now.UtcDateTime.AddSeconds(-60),
            Expires = now.UtcDateTime.AddMinutes(10),
            SigningCredentials = credentials
        };

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateToken(descriptor);

        _logger.LogDebug("Generated JWT for GitHub App {AppId}", _options.AppId);

        return handler.WriteToken(token);
    }

    /// <summary>
    /// Mints a fresh installation access token by posting to GitHub's
    /// <c>/app/installations/{id}/access_tokens</c> endpoint. The returned
    /// record carries the server-reported <c>expires_at</c> so the caller can
    /// drive proactive refresh off the real TTL.
    /// </summary>
    /// <param name="installationId">The GitHub App installation ID.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The minted token with its actual expiration timestamp.</returns>
    public virtual async Task<InstallationAccessToken> MintInstallationTokenAsync(
        long installationId,
        CancellationToken cancellationToken = default)
    {
        var jwt = GenerateJwt();

        // Route through IHttpClientFactory when available so the host-wired
        // credential-health watchdog (and any future cross-cutting handler
        // the host attaches via AddHttpClient(HttpClientName)) sees the
        // response. Fallback to a one-shot HttpClient is kept so direct-
        // construction callers (unit tests) continue to work.
        HttpClient httpClient;
        bool ownsClient;
        if (_httpClientFactory is not null)
        {
            httpClient = _httpClientFactory.CreateClient(HttpClientName);
            ownsClient = false;
        }
        else
        {
            httpClient = new HttpClient();
            ownsClient = true;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://api.github.com/app/installations/{installationId}/access_tokens");
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {jwt}");
            request.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("User-Agent", "SpringVoyage-GitHubConnector");

            var response = await httpClient.SendAsync(request, cancellationToken);

            response.EnsureSuccessStatusCode();

            return await ParseTokenResponseAsync(response, installationId, cancellationToken);
        }
        finally
        {
            if (ownsClient)
            {
                httpClient.Dispose();
            }
        }
    }

    private async Task<InstallationAccessToken> ParseTokenResponseAsync(
        HttpResponseMessage response,
        long installationId,
        CancellationToken cancellationToken)
    {
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var token = root.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("GitHub API returned a null token.");

        // `expires_at` is ISO-8601 UTC. If GitHub ever drops it, we conservatively
        // fall back to 55 minutes (one hour minus a small safety margin) so the
        // cache's refresh window still applies.
        var expiresAt = root.TryGetProperty("expires_at", out var exp)
            && exp.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(exp.GetString(), out var parsed)
                ? parsed
                : _timeProvider.GetUtcNow().AddMinutes(55);

        _logger.LogInformation(
            "Minted installation token for installation {InstallationId}; expires_at={ExpiresAt:o}",
            installationId, expiresAt);

        return new InstallationAccessToken(token, expiresAt);
    }

    /// <summary>
    /// Backwards-compatible overload that returns just the token string. New
    /// callers should use <see cref="MintInstallationTokenAsync"/> and flow
    /// the minted token through <see cref="IInstallationTokenCache"/>.
    /// </summary>
    /// <param name="installationId">The GitHub App installation ID.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The installation access token.</returns>
    public async Task<string> GetInstallationTokenAsync(
        long installationId,
        CancellationToken cancellationToken = default)
    {
        var minted = await MintInstallationTokenAsync(installationId, cancellationToken);
        return minted.Token;
    }

    /// <summary>
    /// Disposes the cached RSA key, if any. The DI container disposes
    /// singletons that implement <see cref="IDisposable"/> on shutdown.
    /// Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Disposes managed resources. Idempotent — repeated calls are no-ops.
    /// </summary>
    /// <param name="disposing">True when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (disposing && _rsa.IsValueCreated)
        {
            _rsa.Value.Dispose();
        }
    }
}