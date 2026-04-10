// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Cvoya.Spring.Dapr.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Cookie-based authentication handler that validates session cookies
/// by looking up the associated user in the database.
/// </summary>
public class OAuthAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IServiceScopeFactory scopeFactory)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    /// <summary>The name of the session cookie used for OAuth authentication.</summary>
    public const string SessionCookieName = "spring_session";

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Cookies.TryGetValue(SessionCookieName, out var sessionValue)
            || string.IsNullOrEmpty(sessionValue))
        {
            return AuthenticateResult.NoResult();
        }

        if (!Guid.TryParse(sessionValue, out var userId))
        {
            return AuthenticateResult.Fail("Invalid session cookie.");
        }

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var user = await dbContext.Users
            .FirstOrDefaultAsync(u => u.Id == userId, Context.RequestAborted);

        if (user is null)
        {
            return AuthenticateResult.Fail("User not found.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.DisplayName),
            new("github_id", user.GitHubId),
            new("github_login", user.GitHubLogin)
        };

        if (!string.IsNullOrEmpty(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        if (!string.IsNullOrEmpty(user.AvatarUrl))
        {
            claims.Add(new Claim("avatar_url", user.AvatarUrl));
        }

        var identity = new ClaimsIdentity(claims, AuthConstants.OAuthScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AuthConstants.OAuthScheme);

        return AuthenticateResult.Success(ticket);
    }
}
