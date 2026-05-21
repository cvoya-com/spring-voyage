// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Auth;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Runtime;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

/// <summary>
/// Validates per-invocation callback JWTs minted by
/// <see cref="CallbackTokenIssuer"/>. Returns the deserialised
/// <see cref="CallbackToken"/> on success; throws
/// <see cref="CallbackTokenValidationException"/> on any integrity, expiry,
/// or claim-shape failure.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0039 §3, this class is the first gate on every dispatcher
/// orchestration callback. It checks signature, expiry, issuer / audience,
/// and the presence-and-shape of the five required claims
/// (<c>sv_tid</c>, <c>sv_addr</c>, <c>sv_thread</c>, <c>sv_msg</c>,
/// <c>exp</c>). It does <b>not</b> consult the directory; the
/// caller-is-unit, target-is-direct-child, self-delegation, and depth
/// gates live in the endpoint handler (D13).
/// </para>
/// <para>
/// The validator reads the unverified <c>sv_tid</c> claim first to resolve
/// the per-tenant signing key, then runs the standard
/// <see cref="JwtSecurityTokenHandler.ValidateToken"/> pipeline against
/// that key. Reading an unverified claim to choose a key is a routine JWT
/// pattern (every multi-tenant validator that resolves keys by issuer or
/// tenant claim does the same); the signature check that follows is what
/// guarantees integrity. A token that names a tenant whose key does not
/// validate the signature is rejected with
/// <see cref="CallbackTokenValidationReason.SignatureInvalid"/>.
/// </para>
/// </remarks>
/// <param name="signingKeyProvider">Per-tenant signing-key source.</param>
/// <param name="options">Callback-token options (clock skew tolerance).</param>
public class CallbackTokenValidator(
    ITenantSigningKeyProvider signingKeyProvider,
    IOptions<CallbackTokenOptions> options)
{
    private readonly ITenantSigningKeyProvider _signingKeyProvider = signingKeyProvider;
    private readonly CallbackTokenOptions _options = options.Value;

    /// <summary>
    /// Validates the supplied compact-JWT string and returns the
    /// deserialised claim shape. Throws
    /// <see cref="CallbackTokenValidationException"/> on any failure.
    /// </summary>
    /// <param name="token">The compact-JWT string to validate.</param>
    /// <returns>The deserialised <see cref="CallbackToken"/>.</returns>
    public virtual CallbackToken Validate(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new CallbackTokenValidationException(
                CallbackTokenValidationReason.Malformed,
                "Callback token is empty.");
        }

        var handler = new JwtSecurityTokenHandler();

        if (!handler.CanReadToken(token))
        {
            throw new CallbackTokenValidationException(
                CallbackTokenValidationReason.Malformed,
                "Callback token is not a valid JWT.");
        }

        // Read the unsigned token to extract the tenant claim so we can
        // resolve the per-tenant signing key; the signature check below is
        // what actually establishes integrity.
        JwtSecurityToken unverified;
        try
        {
            unverified = handler.ReadJwtToken(token);
        }
        catch (Exception ex)
        {
            throw new CallbackTokenValidationException(
                CallbackTokenValidationReason.Malformed,
                "Callback token could not be parsed.",
                ex);
        }

        var tenantClaim = unverified.Claims
            .FirstOrDefault(c => c.Type == CallbackTokenIssuer.TenantIdClaim)?.Value;

        if (string.IsNullOrEmpty(tenantClaim) || !GuidFormatter.TryParse(tenantClaim, out var tenantId))
        {
            throw new CallbackTokenValidationException(
                CallbackTokenValidationReason.ClaimMissingOrInvalid,
                $"Callback token is missing or has an invalid '{CallbackTokenIssuer.TenantIdClaim}' claim.");
        }

        byte[] keyBytes;
        try
        {
            keyBytes = _signingKeyProvider.GetSigningKey(tenantId);
        }
        catch (Exception ex)
        {
            // The key provider treats unknown tenants as a configuration
            // error. The validator surfaces it as a signature failure —
            // from the caller's vantage, "we cannot verify this token" is
            // indistinguishable from "we have no key for this tenant," and
            // the cloud overlay's KMS-backed provider may treat unknown
            // tenants as opaque rejections too.
            throw new CallbackTokenValidationException(
                CallbackTokenValidationReason.SignatureInvalid,
                $"No signing key configured for tenant '{GuidFormatter.Format(tenantId)}'.",
                ex);
        }

        var signingKey = new SymmetricSecurityKey(keyBytes);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = CallbackTokenOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = CallbackTokenOptions.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            ClockSkew = _options.ClockSkew,
            ValidAlgorithms = new[] { SecurityAlgorithms.HmacSha256 },
        };

        ClaimsPrincipal principal;
        SecurityToken validatedToken;
        try
        {
            principal = handler.ValidateToken(token, parameters, out validatedToken);
        }
        catch (SecurityTokenExpiredException ex)
        {
            throw new CallbackTokenValidationException(
                CallbackTokenValidationReason.Expired,
                "Callback token has expired.",
                ex);
        }
        catch (SecurityTokenInvalidSignatureException ex)
        {
            throw new CallbackTokenValidationException(
                CallbackTokenValidationReason.SignatureInvalid,
                "Callback token signature is invalid.",
                ex);
        }
        catch (SecurityTokenException ex)
        {
            // Catches issuer/audience/algorithm mismatches, malformed
            // tokens that slipped past CanReadToken, and other JWT-shape
            // problems. None of these reach the handler.
            throw new CallbackTokenValidationException(
                CallbackTokenValidationReason.SignatureInvalid,
                "Callback token failed validation.",
                ex);
        }

        var addressClaim = principal.FindFirst(CallbackTokenIssuer.AgentAddressClaim)?.Value;
        if (string.IsNullOrEmpty(addressClaim) ||
            !Address.TryParse(addressClaim, out var address) ||
            address is null)
        {
            throw new CallbackTokenValidationException(
                CallbackTokenValidationReason.ClaimMissingOrInvalid,
                $"Callback token is missing or has an invalid '{CallbackTokenIssuer.AgentAddressClaim}' claim.");
        }

        var threadClaim = principal.FindFirst(CallbackTokenIssuer.ThreadIdClaim)?.Value;
        if (string.IsNullOrEmpty(threadClaim) || !GuidFormatter.TryParse(threadClaim, out var threadId))
        {
            throw new CallbackTokenValidationException(
                CallbackTokenValidationReason.ClaimMissingOrInvalid,
                $"Callback token is missing or has an invalid '{CallbackTokenIssuer.ThreadIdClaim}' claim.");
        }

        var messageClaim = principal.FindFirst(CallbackTokenIssuer.MessageIdClaim)?.Value;
        if (string.IsNullOrEmpty(messageClaim) || !GuidFormatter.TryParse(messageClaim, out var messageId))
        {
            throw new CallbackTokenValidationException(
                CallbackTokenValidationReason.ClaimMissingOrInvalid,
                $"Callback token is missing or has an invalid '{CallbackTokenIssuer.MessageIdClaim}' claim.");
        }

        var expiresAt = new DateTimeOffset(validatedToken.ValidTo, TimeSpan.Zero);

        return new CallbackToken(
            tenantId,
            address,
            threadId,
            messageId,
            expiresAt);
    }
}
