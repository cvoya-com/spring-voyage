// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Skills;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Auth.OAuth;

using Microsoft.Extensions.Logging;

/// <summary>
/// Proof-of-wiring skill for the OAuth client factory — takes a session id
/// and returns the authenticated user's profile via <c>GET /user</c>. Keeps
/// the payload small (login, id, email, name) so the demo output is
/// digestible; richer skills build on the same factory.
/// </summary>
public class GetAuthenticatedUserSkill
{
    private readonly IGitHubOAuthClientFactory _factory;
    private readonly ILogger _logger;

    /// <summary>Creates the skill.</summary>
    public GetAuthenticatedUserSkill(
        IGitHubOAuthClientFactory factory,
        ILoggerFactory loggerFactory)
    {
        _factory = factory;
        _logger = loggerFactory.CreateLogger<GetAuthenticatedUserSkill>();
    }

    /// <summary>
    /// Returns the authenticated user's profile for the session.
    /// </summary>
    /// <param name="sessionId">The OAuth session id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<JsonElement> ExecuteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Resolving authenticated user for OAuth session {SessionId}", sessionId);

        var client = await _factory.CreateAsync(sessionId, cancellationToken);
        var user = await client.User.Current();

        var result = new
        {
            login = user.Login,
            id = user.Id,
            name = user.Name,
            email = user.Email,
        };
        return JsonSerializer.SerializeToElement(result);
    }
}