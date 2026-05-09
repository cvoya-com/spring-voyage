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
/// Post #1629: every <see cref="Cvoya.Spring.Core.Messaging.Address"/> is a
/// Guid identity (no slug/identity dichotomy), so the test asserts the
/// address scheme and Guid path directly.
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

        result.Scheme.ShouldBe("human");
        result.Id.ShouldBe(AliceId);
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
