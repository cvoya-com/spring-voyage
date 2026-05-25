// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Auth;

using System.Security.Claims;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Host.Api.Auth;

using Microsoft.AspNetCore.Http;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AuthenticatedCallerAccessor"/>.
///
/// Per ADR-0047 §1 and #2768 the OSS-default accessor returns the canonical
/// <c>tenant-user://&lt;OssTenantUserIds.Operator&gt;</c> address whenever
/// the principal is authenticated and carries a non-empty
/// <see cref="ClaimTypes.NameIdentifier"/> claim. No HumanEntity is
/// auto-minted on login.
/// </summary>
public class AuthenticatedCallerAccessorTests
{
    [Fact]
    public async Task GetCallerAddressAsync_AuthenticatedPrincipal_ReturnsOperatorTenantUserAddress()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.NameIdentifier, "alice") },
            authenticationType: "test");
        httpContext.User = new ClaimsPrincipal(identity);
        accessor.HttpContext.Returns(httpContext);

        var sut = new AuthenticatedCallerAccessor(accessor);

        var result = await sut.GetCallerAddressAsync(TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Scheme.ShouldBe(Address.TenantUserScheme);
        result.Id.ShouldBe(OssTenantUserIds.Operator);
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

        var sut = new AuthenticatedCallerAccessor(accessor);

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

        var sut = new AuthenticatedCallerAccessor(accessor);

        var result = await sut.GetCallerAddressAsync(TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetCallerAddressAsync_AuthenticatedButNoNameIdentifierClaim_ReturnsNull()
    {
        // Defence-in-depth: an authenticated principal without a
        // NameIdentifier claim must NOT silently resolve to the operator.
        // The OSS LocalDev handler always stamps one; this guards against a
        // misconfigured upstream that authenticated without surfacing a
        // subject.
        var accessor = Substitute.For<IHttpContextAccessor>();
        var httpContext = new DefaultHttpContext();
        var identity = new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Name, "alice-display") },
            authenticationType: "test");
        httpContext.User = new ClaimsPrincipal(identity);
        accessor.HttpContext.Returns(httpContext);

        var sut = new AuthenticatedCallerAccessor(accessor);

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

        var sut = new AuthenticatedCallerAccessor(accessor);

        var result = sut.GetUsername();

        result.ShouldBe("alice");
    }

    [Fact]
    public void GetUsername_NoHttpContext_ReturnsFallback()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);

        var sut = new AuthenticatedCallerAccessor(accessor);

        var result = sut.GetUsername();

        result.ShouldBe(AuthenticatedCallerAccessor.FallbackHumanUsername);
    }
}
