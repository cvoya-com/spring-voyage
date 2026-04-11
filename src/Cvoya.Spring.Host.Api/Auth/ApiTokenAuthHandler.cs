// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

using Cvoya.Spring.Dapr.Data;

using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// ASP.NET Core authentication handler that validates bearer tokens against
/// hashed API tokens stored in the database.
/// </summary>
public class ApiTokenAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IServiceScopeFactory scopeFactory)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorizationHeader = Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(authorizationHeader))
        {
            return AuthenticateResult.NoResult();
        }

        if (!authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();

        if (string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.NoResult();
        }

        var tokenHash = HashToken(token);

        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var tokenEntity = await dbContext.ApiTokens
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, Context.RequestAborted);

        if (tokenEntity is null)
        {
            return AuthenticateResult.Fail("Invalid token.");
        }

        if (tokenEntity.RevokedAt.HasValue)
        {
            return AuthenticateResult.Fail("Token has been revoked.");
        }

        if (tokenEntity.ExpiresAt.HasValue && tokenEntity.ExpiresAt.Value <= DateTimeOffset.UtcNow)
        {
            return AuthenticateResult.Fail("Token has expired.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, tokenEntity.UserId ?? tokenEntity.Id.ToString()),
            new("token_name", tokenEntity.Name)
        };

        if (!string.IsNullOrEmpty(tokenEntity.Scopes))
        {
            foreach (var scope2 in tokenEntity.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                claims.Add(new Claim("scope", scope2));
            }
        }

        var identity = new ClaimsIdentity(claims, AuthConstants.ApiTokenScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AuthConstants.ApiTokenScheme);

        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Computes the SHA-256 hash of a raw token string for storage comparison.
    /// </summary>
    /// <param name="rawToken">The raw token value.</param>
    /// <returns>The hex-encoded SHA-256 hash.</returns>
    public static string HashToken(string rawToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        return Convert.ToHexStringLower(bytes);
    }
}