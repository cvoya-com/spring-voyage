// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Auth;

using System.Security.Claims;

using Cvoya.Spring.Host.Api.Auth;

using Microsoft.AspNetCore.Http;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AuthenticatedCallerAccessor"/>. Verifies the
/// #339 fallback semantics: authenticated subjects surface as
/// <c>human://{nameIdentifier}</c>, anonymous / out-of-request contexts
/// fall back to the synthetic <c>human://api</c>.
/// </summary>
public class AuthenticatedCallerAccessorTests
{
    [Fact]
    public void GetHumanAddress_AuthenticatedPrincipal_ReturnsNameIdentifierHuman()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "alice") },
            authenticationType: "test");
        httpContext.User = new ClaimsPrincipal(identity);
        accessor.HttpContext.Returns(httpContext);

        var sut = new AuthenticatedCallerAccessor(accessor);

        var result = sut.GetHumanAddress();

        result.Scheme.ShouldBe("human");
        result.Path.ShouldBe("alice");
    }

    [Fact]
    public void GetHumanAddress_NoHttpContext_FallsBackToApi()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);

        var sut = new AuthenticatedCallerAccessor(accessor);

        var result = sut.GetHumanAddress();

        result.Scheme.ShouldBe("human");
        result.Path.ShouldBe(AuthenticatedCallerAccessor.FallbackHumanId);
    }

    [Fact]
    public void GetHumanAddress_AnonymousPrincipal_FallsBackToApi()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()),
        };
        accessor.HttpContext.Returns(httpContext);

        var sut = new AuthenticatedCallerAccessor(accessor);

        var result = sut.GetHumanAddress();

        result.Scheme.ShouldBe("human");
        result.Path.ShouldBe(AuthenticatedCallerAccessor.FallbackHumanId);
    }

    [Fact]
    public void GetHumanAddress_AuthenticatedButMissingNameIdentifier_FallsBackToApi()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "alice") },
            authenticationType: "test");
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(identity),
        };
        accessor.HttpContext.Returns(httpContext);

        var sut = new AuthenticatedCallerAccessor(accessor);

        var result = sut.GetHumanAddress();

        result.Scheme.ShouldBe("human");
        result.Path.ShouldBe(AuthenticatedCallerAccessor.FallbackHumanId);
    }
}