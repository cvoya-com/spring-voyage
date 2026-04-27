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
/// <remarks>
/// Role claims are appended via the registered <see cref="IRoleClaimSource"/>.
/// The OSS default grants every authenticated caller all three platform
/// roles; the cloud overlay (private repo) supplies its own
/// <see cref="IRoleClaimSource"/> via <c>TryAddSingleton</c> ahead of the
/// OSS extension call so its identity-scoped subset wins.
/// </remarks>
public class LocalDevAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IRoleClaimSource roleClaimSource)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, AuthConstants.DefaultLocalUserId),
            new(ClaimTypes.Name, "Local Developer"),
        };

        var identity = new ClaimsIdentity(claims, AuthConstants.LocalDevScheme);

        // Append platform-role claims emitted by the registered source.
        // OSS grants all three; cloud overlays scope per identity.
        identity.AddClaims(roleClaimSource.GetRoleClaims(identity));

        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AuthConstants.LocalDevScheme);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}