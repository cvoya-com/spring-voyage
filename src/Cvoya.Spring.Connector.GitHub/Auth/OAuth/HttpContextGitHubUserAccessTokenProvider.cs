// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using Cvoya.Spring.Core.Secrets;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default OSS <see cref="IGitHubUserAccessTokenProvider"/>. Resolves the
/// signed-in GitHub identity by:
/// <list type="number">
///   <item><description>Reading an <c>oauth_session_id</c> query parameter
///   off the ambient <see cref="HttpContext"/>; if absent, falls back to the
///   <c>X-GitHub-OAuth-Session</c> request header.</description></item>
///   <item><description>Looking the session up via
///   <see cref="IOAuthSessionStore"/>.</description></item>
///   <item><description>Reading the access-token plaintext via
///   <see cref="ISecretStore"/>.</description></item>
/// </list>
/// Returns <c>null</c> at any step that fails — callers (e.g. the
/// repository-listing endpoint) treat that as "the request did not carry a
/// signed-in GitHub user".
/// </summary>
/// <remarks>
/// Wiring: registered as <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Scoped"/>
/// because the lookup is per-request. The session and secret stores are
/// process-singletons in the OSS default; the cloud overlay swaps both for
/// tenant-scoped persistent variants.
/// </remarks>
public class HttpContextGitHubUserAccessTokenProvider : IGitHubUserAccessTokenProvider
{
    /// <summary>
    /// Query string key the wizard appends to <c>list-repositories</c> so
    /// the connector knows which OAuth session is in flight. Kept in one
    /// place so the wizard's TypeScript and the connector's C# stay in sync.
    /// </summary>
    public const string SessionIdQueryKey = "oauth_session_id";

    /// <summary>
    /// Equivalent header name. Useful for non-browser clients (the CLI) that
    /// would rather thread the session id through a header than a query
    /// parameter.
    /// </summary>
    public const string SessionIdHeaderKey = "X-GitHub-OAuth-Session";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IOAuthSessionStore _sessionStore;
    private readonly ISecretStore _secretStore;
    private readonly ILogger _logger;

    /// <summary>Creates a new provider.</summary>
    public HttpContextGitHubUserAccessTokenProvider(
        IHttpContextAccessor httpContextAccessor,
        IOAuthSessionStore sessionStore,
        ISecretStore secretStore,
        ILoggerFactory loggerFactory)
    {
        _httpContextAccessor = httpContextAccessor;
        _sessionStore = sessionStore;
        _secretStore = secretStore;
        _logger = loggerFactory.CreateLogger<HttpContextGitHubUserAccessTokenProvider>();
    }

    /// <inheritdoc />
    public async Task<GitHubUserAccess?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var sessionId = ReadSessionId();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var session = await _sessionStore.GetAsync(sessionId, cancellationToken);
        if (session is null)
        {
            // Unknown / expired session — treat as not signed in. The
            // wizard surfaces this as "Sign in with GitHub" so the user
            // can re-authorise.
            _logger.LogInformation(
                "OAuth session {SessionId} not found while resolving GitHub user access token",
                sessionId);
            return null;
        }

        var accessToken = await _secretStore.ReadAsync(session.AccessTokenStoreKey, cancellationToken);
        if (string.IsNullOrEmpty(accessToken))
        {
            _logger.LogWarning(
                "OAuth session {SessionId} resolved but its access-token store entry is missing; treating as signed-out",
                sessionId);
            return null;
        }

        return new GitHubUserAccess(session.Login, session.UserId, accessToken);
    }

    private string? ReadSessionId()
    {
        var http = _httpContextAccessor.HttpContext;
        if (http is null)
        {
            return null;
        }

        var fromQuery = http.Request.Query[SessionIdQueryKey].ToString();
        if (!string.IsNullOrWhiteSpace(fromQuery))
        {
            return fromQuery;
        }

        var fromHeader = http.Request.Headers[SessionIdHeaderKey].ToString();
        if (!string.IsNullOrWhiteSpace(fromHeader))
        {
            return fromHeader;
        }

        return null;
    }
}