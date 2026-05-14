// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Tests.Auth;

using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Runtime;
using Cvoya.Spring.Dispatcher.Auth;

using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the callback-token sign/validate round-trip and the four failure
/// modes the validator must surface as
/// <see cref="CallbackTokenValidationException"/>: expiry, scope mismatch
/// (token signed for tenant A presented as tenant B), tampered signature,
/// and missing claims. The validator does not consult the directory; that
/// is D13's responsibility (covered separately in endpoint tests).
/// </summary>
public class CallbackTokenIssuerValidatorTests
{
    private static byte[] NewKey() => RandomNumberGenerator.GetBytes(32);

    private static IOptions<CallbackTokenOptions> Options(TimeSpan? lifetime = null, TimeSpan? skew = null)
    {
        var opts = new CallbackTokenOptions();
        if (lifetime is not null)
        {
            opts.Lifetime = lifetime.Value;
        }

        if (skew is not null)
        {
            opts.ClockSkew = skew.Value;
        }

        return Microsoft.Extensions.Options.Options.Create(opts);
    }

    private static CallbackToken NewClaims(
        Guid? tenantId = null,
        Address? address = null,
        Guid? threadId = null,
        Guid? messageId = null,
        DateTimeOffset expiresAt = default)
    {
        return new CallbackToken(
            tenantId ?? Guid.NewGuid(),
            address ?? new Address(Address.UnitScheme, Guid.NewGuid()),
            threadId ?? Guid.NewGuid(),
            messageId ?? Guid.NewGuid(),
            expiresAt);
    }

    [Fact]
    public void Issue_ThenValidate_RoundtripsClaims()
    {
        var tenantId = Guid.NewGuid();
        var key = NewKey();
        var keyProvider = Substitute.For<ITenantSigningKeyProvider>();
        keyProvider.GetSigningKey(tenantId).Returns(key);

        var options = Options();
        var issuer = new CallbackTokenIssuer(keyProvider, options);
        var validator = new CallbackTokenValidator(keyProvider, options);

        var address = new Address(Address.UnitScheme, Guid.NewGuid());
        var threadId = Guid.NewGuid();
        var messageId = Guid.NewGuid();

        var claims = NewClaims(
            tenantId: tenantId,
            address: address,
            threadId: threadId,
            messageId: messageId);

        var jwt = issuer.Issue(claims);
        jwt.ShouldNotBeNullOrEmpty();

        var roundtripped = validator.Validate(jwt);

        roundtripped.TenantId.ShouldBe(tenantId);
        roundtripped.AgentAddress.ShouldBe(address);
        roundtripped.ThreadId.ShouldBe(threadId);
        roundtripped.MessageId.ShouldBe(messageId);
        // Default lifetime is 5 minutes; assert the issuer stamped a future expiry.
        roundtripped.ExpiresAt.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Validate_ExpiredToken_ThrowsExpired()
    {
        var tenantId = Guid.NewGuid();
        var key = NewKey();
        var keyProvider = Substitute.For<ITenantSigningKeyProvider>();
        keyProvider.GetSigningKey(tenantId).Returns(key);

        // Tiny lifetime, zero skew so the second validation call is past expiry.
        var options = Options(lifetime: TimeSpan.FromSeconds(1), skew: TimeSpan.Zero);

        var fixedNow = DateTimeOffset.UtcNow.AddYears(-1);
        var fakeClock = new FakeTimeProvider(fixedNow);

        var issuer = new CallbackTokenIssuer(keyProvider, options, fakeClock);
        var validator = new CallbackTokenValidator(keyProvider, options);

        var jwt = issuer.Issue(NewClaims(tenantId: tenantId));

        var ex = Should.Throw<CallbackTokenValidationException>(() => validator.Validate(jwt));
        ex.Reason.ShouldBe(CallbackTokenValidationReason.Expired);
    }

    [Fact]
    public void Validate_TamperedSignature_ThrowsSignatureInvalid()
    {
        var tenantId = Guid.NewGuid();
        var key = NewKey();
        var keyProvider = Substitute.For<ITenantSigningKeyProvider>();
        keyProvider.GetSigningKey(tenantId).Returns(key);

        var options = Options();
        var issuer = new CallbackTokenIssuer(keyProvider, options);
        var validator = new CallbackTokenValidator(keyProvider, options);

        var jwt = issuer.Issue(NewClaims(tenantId: tenantId));

        // Flip a bit in the signature segment. JWT compact form is
        // header.payload.signature; mutate the last segment so the HMAC
        // no longer matches.
        var parts = jwt.Split('.');
        parts.Length.ShouldBe(3);
        var sigBytes = Base64UrlDecode(parts[2]);
        sigBytes[0] ^= 0xFF;
        parts[2] = Base64UrlEncode(sigBytes);
        var tampered = string.Join('.', parts);

        var ex = Should.Throw<CallbackTokenValidationException>(() => validator.Validate(tampered));
        ex.Reason.ShouldBe(CallbackTokenValidationReason.SignatureInvalid);
    }

    [Fact]
    public void Validate_TenantMismatch_ThrowsSignatureInvalid()
    {
        // Scope mismatch: a token whose tenant claim is mutated post-signing
        // resolves to a different tenant key, so the HMAC no longer
        // verifies. This exercises both the "claim is tenant A but key is
        // tenant B" path and the underlying integrity guarantee — without
        // signing-key separation across tenants, a token issued for tenant
        // A would happily validate for tenant B.
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var keyA = NewKey();
        var keyB = NewKey();

        var keyProvider = Substitute.For<ITenantSigningKeyProvider>();
        keyProvider.GetSigningKey(tenantA).Returns(keyA);
        keyProvider.GetSigningKey(tenantB).Returns(keyB);

        var options = Options();
        var issuer = new CallbackTokenIssuer(keyProvider, options);
        var validator = new CallbackTokenValidator(keyProvider, options);

        var jwt = issuer.Issue(NewClaims(tenantId: tenantA));

        // Mutate the payload claim to point at tenantB (post-signing).
        var parts = jwt.Split('.');
        parts.Length.ShouldBe(3);
        var payload = System.Text.Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        var newPayload = payload.Replace(
            $"\"{CallbackTokenIssuer.TenantIdClaim}\":\"{GuidFormatter.Format(tenantA)}\"",
            $"\"{CallbackTokenIssuer.TenantIdClaim}\":\"{GuidFormatter.Format(tenantB)}\"",
            StringComparison.Ordinal);
        newPayload.ShouldNotBe(payload, "the substitution must actually change the payload");
        parts[1] = Base64UrlEncode(System.Text.Encoding.UTF8.GetBytes(newPayload));
        var tampered = string.Join('.', parts);

        var ex = Should.Throw<CallbackTokenValidationException>(() => validator.Validate(tampered));
        ex.Reason.ShouldBe(CallbackTokenValidationReason.SignatureInvalid);
    }

    [Fact]
    public void Validate_MalformedToken_ThrowsMalformed()
    {
        var keyProvider = Substitute.For<ITenantSigningKeyProvider>();
        var validator = new CallbackTokenValidator(keyProvider, Options());

        var ex = Should.Throw<CallbackTokenValidationException>(() => validator.Validate("not-a-jwt"));
        ex.Reason.ShouldBe(CallbackTokenValidationReason.Malformed);
    }

    [Fact]
    public void Validate_EmptyToken_ThrowsMalformed()
    {
        var keyProvider = Substitute.For<ITenantSigningKeyProvider>();
        var validator = new CallbackTokenValidator(keyProvider, Options());

        var ex = Should.Throw<CallbackTokenValidationException>(() => validator.Validate(string.Empty));
        ex.Reason.ShouldBe(CallbackTokenValidationReason.Malformed);
    }

    [Fact]
    public void Validate_MissingTenantClaim_ThrowsClaimMissingOrInvalid()
    {
        // Hand-roll a JWT that omits sv_tid; the validator must reject it
        // before the signature path can even try to resolve a key.
        var key = NewKey();
        var signing = new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(key);
        var creds = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            signing,
            Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var handler = new JwtSecurityTokenHandler();
        var token = handler.CreateJwtSecurityToken(
            issuer: CallbackTokenOptions.Issuer,
            audience: CallbackTokenOptions.Audience,
            subject: new System.Security.Claims.ClaimsIdentity(new[]
            {
                // Deliberately omit sv_tid.
                new System.Security.Claims.Claim(
                    CallbackTokenIssuer.AgentAddressClaim,
                    new Address(Address.UnitScheme, Guid.NewGuid()).ToString()),
            }),
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(5),
            issuedAt: DateTime.UtcNow,
            signingCredentials: creds);

        var jwt = handler.WriteToken(token);

        var keyProvider = Substitute.For<ITenantSigningKeyProvider>();
        var validator = new CallbackTokenValidator(keyProvider, Options());

        var ex = Should.Throw<CallbackTokenValidationException>(() => validator.Validate(jwt));
        ex.Reason.ShouldBe(CallbackTokenValidationReason.ClaimMissingOrInvalid);

        // Without a tenant claim, the validator must not even ask the key
        // provider for a key (no tenant to ask for).
        keyProvider.DidNotReceive().GetSigningKey(Arg.Any<Guid>());
    }

    [Fact]
    public void Validate_DoesNotConsultDirectory()
    {
        // The contract: "Validation only checks token integrity and claim
        // shape — the caller-has-children and target-is-child authorization
        // checks live in the endpoint handlers (D13)." This is enforced by
        // construction — the validator's only collaborator is
        // ITenantSigningKeyProvider — but pin it explicitly so a future
        // refactor that adds a directory dependency to the validator
        // breaks this test.
        var validatorType = typeof(CallbackTokenValidator);
        var ctors = validatorType.GetConstructors();
        ctors.Length.ShouldBe(1);
        var paramTypes = Array.ConvertAll(ctors[0].GetParameters(), p => p.ParameterType);

        paramTypes.ShouldContain(typeof(ITenantSigningKeyProvider));
        paramTypes.ShouldContain(typeof(IOptions<CallbackTokenOptions>));
        paramTypes.Length.ShouldBe(2);
    }

    private static byte[] Base64UrlDecode(string segment)
    {
        var padded = segment.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private readonly DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
