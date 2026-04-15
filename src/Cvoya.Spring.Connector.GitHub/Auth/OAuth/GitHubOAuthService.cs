// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using System.Security.Cryptography;

using Cvoya.Spring.Core.Secrets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Octokit;

/// <summary>
/// Default <see cref="IGitHubOAuthService"/>. Owns the cryptographic state
/// and session-id generation so callers never stamp their own random
/// values.
/// </summary>
public class GitHubOAuthService : IGitHubOAuthService
{
    private static readonly ProductHeaderValue UserAgent = new("SpringVoyage-GitHubConnector");
    private const string AuthorizeEndpoint = "https://github.com/login/oauth/authorize";

    private readonly IOAuthStateStore _stateStore;
    private readonly IOAuthSessionStore _sessionStore;
    private readonly IGitHubOAuthHttpClient _oauthHttp;
    private readonly ISecretStore _secretStore;
    private readonly IOptionsMonitor<GitHubOAuthOptions> _options;
    private readonly IGitHubUserFetcher _userFetcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    /// <summary>Creates a new service.</summary>
    public GitHubOAuthService(
        IOAuthStateStore stateStore,
        IOAuthSessionStore sessionStore,
        IGitHubOAuthHttpClient oauthHttp,
        ISecretStore secretStore,
        IOptionsMonitor<GitHubOAuthOptions> options,
        IGitHubUserFetcher userFetcher,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null)
    {
        _stateStore = stateStore;
        _sessionStore = sessionStore;
        _oauthHttp = oauthHttp;
        _secretStore = secretStore;
        _options = options;
        _userFetcher = userFetcher;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<GitHubOAuthService>();
    }

    /// <inheritdoc />
    public async Task<AuthorizeResult> BeginAuthorizationAsync(
        IReadOnlyList<string>? scopesOverride,
        string? clientState,
        CancellationToken ct)
    {
        var options = _options.CurrentValue;
        EnsureConfigured(options, requireSecret: false);

        var state = GenerateRandomToken(16);
        var scopesList = scopesOverride is { Count: > 0 } ? scopesOverride : (IReadOnlyList<string>)options.Scopes;
        var scopes = string.Join(' ', scopesList);

        var entry = new OAuthStateEntry(
            State: state,
            Scopes: scopes,
            RedirectUri: options.RedirectUri,
            ExpiresAt: _timeProvider.GetUtcNow().Add(options.StateTtl),
            ClientState: clientState);
        await _stateStore.SaveAsync(entry, ct);

        var query = new List<string>
        {
            $"client_id={Uri.EscapeDataString(options.ClientId)}",
            $"redirect_uri={Uri.EscapeDataString(options.RedirectUri)}",
            $"state={Uri.EscapeDataString(state)}",
        };
        if (!string.IsNullOrEmpty(scopes))
        {
            query.Add($"scope={Uri.EscapeDataString(scopes)}");
        }

        var url = $"{AuthorizeEndpoint}?{string.Join('&', query)}";
        _logger.LogInformation(
            "Issued GitHub OAuth authorize URL (state prefix={StatePrefix}, scopes={Scopes})",
            state[..Math.Min(6, state.Length)], scopes);
        return new AuthorizeResult(url, state);
    }

    /// <inheritdoc />
    public async Task<CallbackResult> HandleCallbackAsync(
        string code,
        string state,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return new CallbackResult(null, null, "invalid_request", "Both 'code' and 'state' are required.");
        }

        var options = _options.CurrentValue;
        EnsureConfigured(options, requireSecret: true);

        var entry = await _stateStore.ConsumeAsync(state, ct);
        if (entry is null)
        {
            return new CallbackResult(null, null, "invalid_state", "The state parameter is unknown or has expired.");
        }

        var exchange = await _oauthHttp.ExchangeCodeAsync(
            options.ClientId, options.ClientSecret, code, entry.RedirectUri, ct);

        if (!string.IsNullOrEmpty(exchange.Error) || string.IsNullOrEmpty(exchange.AccessToken))
        {
            _logger.LogWarning(
                "OAuth code exchange failed: {Error} ({ErrorDescription})",
                exchange.Error, exchange.ErrorDescription);
            return new CallbackResult(null, null, exchange.Error ?? "exchange_failed", exchange.ErrorDescription);
        }

        // Look up the user identity now so the session metadata lands with
        // a login attached. Doing this with the freshly-issued token
        // doubles as a sanity check — if GitHub won't tell us who we are,
        // the token is useless.
        GitHubUserIdentity identity;
        try
        {
            identity = await _userFetcher.GetAsync(exchange.AccessToken, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve /user for freshly-issued OAuth token");
            return new CallbackResult(null, null, "user_fetch_failed", ex.Message);
        }

        var accessKey = await _secretStore.WriteAsync(exchange.AccessToken, ct);
        string? refreshKey = null;
        if (!string.IsNullOrEmpty(exchange.RefreshToken))
        {
            refreshKey = await _secretStore.WriteAsync(exchange.RefreshToken, ct);
        }

        var sessionId = GenerateRandomToken(16);
        var session = new OAuthSession(
            SessionId: sessionId,
            Login: identity.Login,
            UserId: identity.Id,
            Scopes: exchange.GrantedScopes,
            AccessTokenStoreKey: accessKey,
            RefreshTokenStoreKey: refreshKey,
            ExpiresAt: exchange.ExpiresAt,
            CreatedAt: _timeProvider.GetUtcNow(),
            ClientState: entry.ClientState);

        await _sessionStore.SaveAsync(session, ct);

        _logger.LogInformation(
            "OAuth session {SessionId} created for login {Login}",
            sessionId, identity.Login);

        return new CallbackResult(sessionId, identity.Login, null, null);
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(string sessionId, CancellationToken ct)
    {
        var session = await _sessionStore.GetAsync(sessionId, ct);
        if (session is null)
        {
            return false;
        }

        var options = _options.CurrentValue;
        EnsureConfigured(options, requireSecret: true);

        var accessToken = await _secretStore.ReadAsync(session.AccessTokenStoreKey, ct);
        if (!string.IsNullOrEmpty(accessToken))
        {
            // Best-effort remote revoke; the local record is always cleared
            // regardless of the outcome so stale entries can't accumulate.
            try
            {
                await _oauthHttp.RevokeTokenAsync(options.ClientId, options.ClientSecret, accessToken, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "OAuth token revocation failed for session {SessionId}; continuing with local cleanup",
                    sessionId);
            }
        }

        await _secretStore.DeleteAsync(session.AccessTokenStoreKey, ct);
        if (!string.IsNullOrEmpty(session.RefreshTokenStoreKey))
        {
            await _secretStore.DeleteAsync(session.RefreshTokenStoreKey, ct);
        }
        await _sessionStore.DeleteAsync(sessionId, ct);

        _logger.LogInformation("OAuth session {SessionId} revoked", sessionId);
        return true;
    }

    /// <inheritdoc />
    public Task<OAuthSession?> GetSessionAsync(string sessionId, CancellationToken ct)
        => _sessionStore.GetAsync(sessionId, ct);

    private static void EnsureConfigured(GitHubOAuthOptions options, bool requireSecret)
    {
        if (string.IsNullOrWhiteSpace(options.ClientId))
        {
            throw new InvalidOperationException("GitHub:OAuth:ClientId is not configured.");
        }
        if (string.IsNullOrWhiteSpace(options.RedirectUri))
        {
            throw new InvalidOperationException("GitHub:OAuth:RedirectUri is not configured.");
        }
        if (requireSecret && string.IsNullOrWhiteSpace(options.ClientSecret))
        {
            throw new InvalidOperationException("GitHub:OAuth:ClientSecret is not configured.");
        }
    }

    private static string GenerateRandomToken(int bytes)
    {
        // URL-safe base64 of (bytes) of cryptographic randomness. 16 bytes =
        // 128 bits, which matches the security guidance on the task.
        Span<byte> buffer = stackalloc byte[32];
        if (bytes > buffer.Length)
        {
            buffer = new byte[bytes];
        }
        else
        {
            buffer = buffer[..bytes];
        }
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}