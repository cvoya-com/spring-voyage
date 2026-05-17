// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Auth;

using System.Security.Claims;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Host.Api.Auth;

using Microsoft.AspNetCore.Http;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AuthenticatedCallerAccessor"/>.
///
/// Post-ADR-0036 every <see cref="Cvoya.Spring.Core.Messaging.Address"/> is
/// a Guid identity (no slug/identity dichotomy). #2405 made the
/// "no caller available" outcome explicit at the type level — the accessor
/// returns <see langword="null"/> instead of fabricating a non-Guid
/// fallback address that would throw <c>InvalidAddressIdException</c>.
/// </summary>
public class AuthenticatedCallerAccessorTests
{
    private static readonly Guid AliceId = new("aaaaaaaa-0000-0000-0000-000000000001");

    private readonly IHumanIdentityResolver _identityResolver = Substitute.For<IHumanIdentityResolver>();

    public AuthenticatedCallerAccessorTests()
    {
        // Default: any username resolves to AliceId.
        _identityResolver.ResolveByUsernameAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(AliceId);
    }

    [Fact]
    public async Task GetCallerAddressAsync_AuthenticatedPrincipal_ReturnsResolverGuidAddress()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "alice") },
            authenticationType: "test");
        httpContext.User = new ClaimsPrincipal(identity);
        accessor.HttpContext.Returns(httpContext);

        var sut = new AuthenticatedCallerAccessor(accessor, _identityResolver);

        var result = await sut.GetCallerAddressAsync(TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Scheme.ShouldBe("human");
        result.Id.ShouldBe(AliceId);
    }

    [Fact]
    public async Task GetCallerAddressAsync_NoHttpContext_ReturnsNull()
    {
        // #2405: out-of-request paths (worker, integration tests pre-dating
        // the resolver) surface as a null address rather than throwing
        // InvalidAddressIdException from the pre-ADR-0036 navigation-form
        // fallback.
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);

        var sut = new AuthenticatedCallerAccessor(accessor, _identityResolver);

        var result = await sut.GetCallerAddressAsync(TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetCallerAddressAsync_UnauthenticatedPrincipal_ReturnsNull()
    {
        // Anonymous-handler / pre-auth-pipeline paths: the HTTP context
        // exists but the principal is not authenticated. Same outcome as
        // out-of-request — null, not a synthetic fallback (#2405).
        var accessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        // Default ClaimsPrincipal has no identity → IsAuthenticated == false.
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        accessor.HttpContext.Returns(httpContext);

        var sut = new AuthenticatedCallerAccessor(accessor, _identityResolver);

        var result = await sut.GetCallerAddressAsync(TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetCallerAddressAsync_AuthenticatedButNoNameIdentifierClaim_ReturnsNull()
    {
        // Auth handler accepted the request but did not emit a
        // NameIdentifier claim. Pre-#2405 this hit Address.For("human", "api")
        // and threw post-ADR-0036; now it surfaces null cleanly.
        var accessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "alice-display") },
            authenticationType: "test");
        httpContext.User = new ClaimsPrincipal(identity);
        accessor.HttpContext.Returns(httpContext);

        var sut = new AuthenticatedCallerAccessor(accessor, _identityResolver);

        var result = await sut.GetCallerAddressAsync(TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public void GetUsername_AuthenticatedPrincipal_ReturnsNameIdentifier()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "alice") },
            authenticationType: "test");
        httpContext.User = new ClaimsPrincipal(identity);
        accessor.HttpContext.Returns(httpContext);

        var sut = new AuthenticatedCallerAccessor(accessor, _identityResolver);

        var result = sut.GetUsername();

        result.ShouldBe("alice");
    }

    [Fact]
    public void GetUsername_NoHttpContext_ReturnsFallback()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);

        var sut = new AuthenticatedCallerAccessor(accessor, _identityResolver);

        var result = sut.GetUsername();

        result.ShouldBe(AuthenticatedCallerAccessor.FallbackHumanUsername);
    }
}
