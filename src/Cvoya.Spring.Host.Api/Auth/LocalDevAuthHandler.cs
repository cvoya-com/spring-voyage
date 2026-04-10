// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Auth;

using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Authentication handler that bypasses real authentication in local dev mode.
/// Always authenticates requests as a default local user with full access.
/// </summary>
public class LocalDevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, AuthConstants.DefaultLocalUserId),
            new Claim("tenant_id", AuthConstants.DefaultLocalTenantId),
            new Claim(ClaimTypes.Name, "Local Developer")
        };

        var identity = new ClaimsIdentity(claims, AuthConstants.LocalDevScheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AuthConstants.LocalDevScheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
