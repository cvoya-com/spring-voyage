// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Auth;

using System.Security.Claims;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Services;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the DI seam contract for <see cref="IRoleClaimSource"/>: the
/// OSS host registers <see cref="OssAllRolesClaimSource"/> via
/// <c>TryAddSingleton</c>, so a cloud overlay that pre-registers its own
/// implementation before calling <see cref="ServiceCollectionExtensions.AddCvoyaSpringApiServices"/>
/// keeps its own registration. Mirrors the extensibility rule in
/// <c>AGENTS.md</c> § "Open-source platform and extensibility" and the
/// <c>TryAdd*</c> guidance in <c>CONVENTIONS.md</c> § Dependency Injection.
/// </summary>
public class RoleClaimSourceRegistrationTests
{
    [Fact]
    public void AddCvoyaSpringApiServices_DefaultRegistration_IsOssAllRolesClaimSource()
    {
        var services = new ServiceCollection();
        services.AddCvoyaSpringApiServices(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        var source = provider.GetRequiredService<IRoleClaimSource>();
        source.ShouldBeOfType<OssAllRolesClaimSource>();
    }

    [Fact]
    public void AddCvoyaSpringApiServices_PreRegisteredCloudOverlay_NotDisplaced()
    {
        // Simulate the cloud overlay pre-registering its own claim source
        // before the OSS extension call. TryAddSingleton must respect that.
        var services = new ServiceCollection();
        services.AddSingleton<IRoleClaimSource, ScopedSubsetClaimSource>();

        services.AddCvoyaSpringApiServices(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();

        var source = provider.GetRequiredService<IRoleClaimSource>();
        source.ShouldBeOfType<ScopedSubsetClaimSource>();
    }

    [Fact]
    public void ScopedSubsetClaimSource_ReturnsOnlyRequestedRole()
    {
        // Stand-in for the cloud overlay's per-identity scoping. Confirms
        // the contract from the policy side: a principal carrying only one
        // role passes only the matching policy.
        var sut = new ScopedSubsetClaimSource(PlatformRoles.TenantUser);

        var claims = sut.GetRoleClaims(new ClaimsIdentity()).ToList();

        claims.Count.ShouldBe(1);
        claims[0].Type.ShouldBe(ClaimTypes.Role);
        claims[0].Value.ShouldBe(PlatformRoles.TenantUser);
    }

    /// <summary>
    /// Test stand-in for the cloud overlay's <see cref="IRoleClaimSource"/>.
    /// Returns a single role to mirror "this caller is only a TenantUser".
    /// </summary>
    private sealed class ScopedSubsetClaimSource : IRoleClaimSource
    {
        private readonly string _role;

        public ScopedSubsetClaimSource() : this(PlatformRoles.TenantUser) { }

        public ScopedSubsetClaimSource(string role) => _role = role;

        public IEnumerable<Claim> GetRoleClaims(ClaimsIdentity identity)
        {
            yield return new Claim(ClaimTypes.Role, _role);
        }
    }
}