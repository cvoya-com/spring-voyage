// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Secrets;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Secrets;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="ComposedSecretResolver"/> verifying that it
/// correctly composes <see cref="ISecretRegistry"/>,
/// <see cref="ISecretStore"/>, <see cref="ITenantContext"/>, and
/// <see cref="ISecretAccessPolicy"/> — including the Unit → Tenant
/// inheritance fall-through contract specified in ADR 0003.
/// </summary>
public class ComposedSecretResolverTests
{
    private const string TenantId = "acme";

    private static ComposedSecretResolver CreateSut(
        ISecretRegistry registry,
        ISecretStore store,
        ISecretAccessPolicy? accessPolicy = null,
        bool inheritTenantFromUnit = true,
        string tenantId = TenantId)
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.CurrentTenantId.Returns(tenantId);

        var policy = accessPolicy ?? new AllowAllSecretAccessPolicy();

        var options = Options.Create(new SecretsOptions
        {
            InheritTenantFromUnit = inheritTenantFromUnit,
        });

        return new ComposedSecretResolver(registry, store, tenantContext, policy, options);
    }

    [Fact]
    public async Task ResolveAsync_ExistingUnitRef_ReturnsUnitPlaintextWithoutTenantLookup()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();
        var policy = Substitute.For<ISecretAccessPolicy>();
        policy.IsAuthorizedAsync(Arg.Any<SecretAccessAction>(), Arg.Any<SecretScope>(), Arg.Any<string>(), ct)
            .Returns(true);

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "foo");
        registry.LookupStoreKeyAsync(unitRef, ct).Returns("sk-unit");
        store.ReadAsync("sk-unit", ct).Returns("unit-value");

        var sut = CreateSut(registry, store, policy);

        var result = await sut.ResolveAsync(unitRef, ct);

        result.ShouldBe("unit-value");

        // Tenant scope must not be consulted on a direct hit.
        await registry.DidNotReceive().LookupStoreKeyAsync(
            Arg.Is<SecretRef>(r => r.Scope == SecretScope.Tenant), Arg.Any<CancellationToken>());
        await policy.DidNotReceive().IsAuthorizedAsync(
            Arg.Any<SecretAccessAction>(), SecretScope.Tenant, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveWithPathAsync_ExistingUnitRef_ReportsDirectPath()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "foo");
        registry.LookupStoreKeyAsync(unitRef, ct).Returns("sk-unit");
        store.ReadAsync("sk-unit", ct).Returns("unit-value");

        var sut = CreateSut(registry, store);

        var resolution = await sut.ResolveWithPathAsync(unitRef, ct);

        resolution.Value.ShouldBe("unit-value");
        resolution.Path.ShouldBe(SecretResolvePath.Direct);
        resolution.EffectiveRef.ShouldBe(unitRef);
    }

    [Fact]
    public async Task ResolveAsync_UnitMissesTenantHas_FallsThroughAndChecksBothScopes()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();
        var policy = Substitute.For<ISecretAccessPolicy>();
        policy.IsAuthorizedAsync(Arg.Any<SecretAccessAction>(), Arg.Any<SecretScope>(), Arg.Any<string>(), ct)
            .Returns(true);

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "shared-token");
        var tenantRef = new SecretRef(SecretScope.Tenant, TenantId, "shared-token");
        registry.LookupStoreKeyAsync(unitRef, ct).Returns((string?)null);
        registry.LookupStoreKeyAsync(tenantRef, ct).Returns("sk-tenant");
        store.ReadAsync("sk-tenant", ct).Returns("tenant-value");

        var sut = CreateSut(registry, store, policy);

        var resolution = await sut.ResolveWithPathAsync(unitRef, ct);

        resolution.Value.ShouldBe("tenant-value");
        resolution.Path.ShouldBe(SecretResolvePath.InheritedFromTenant);
        resolution.EffectiveRef.ShouldBe(tenantRef);

        // Access policy must have been called at BOTH scopes with Read.
        await policy.Received(1).IsAuthorizedAsync(
            SecretAccessAction.Read, SecretScope.Unit, "u1", ct);
        await policy.Received(1).IsAuthorizedAsync(
            SecretAccessAction.Read, SecretScope.Tenant, TenantId, ct);
    }

    [Fact]
    public async Task ResolveAsync_UnitAndTenantMiss_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "missing");
        registry.LookupStoreKeyAsync(
            Arg.Any<SecretRef>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        var sut = CreateSut(registry, store);

        var resolution = await sut.ResolveWithPathAsync(unitRef, ct);

        resolution.Value.ShouldBeNull();
        resolution.Path.ShouldBe(SecretResolvePath.NotFound);
        resolution.EffectiveRef.ShouldBeNull();

        await store.DidNotReceive().ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_TenantDeniedDuringFallback_FailsClosed()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();
        var policy = Substitute.For<ISecretAccessPolicy>();

        // Unit read allowed, tenant read denied — the fall-through must
        // NOT leak the tenant plaintext.
        policy.IsAuthorizedAsync(SecretAccessAction.Read, SecretScope.Unit, "u1", ct)
            .Returns(true);
        policy.IsAuthorizedAsync(SecretAccessAction.Read, SecretScope.Tenant, TenantId, ct)
            .Returns(false);

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "shared-token");
        var tenantRef = new SecretRef(SecretScope.Tenant, TenantId, "shared-token");
        registry.LookupStoreKeyAsync(unitRef, ct).Returns((string?)null);
        registry.LookupStoreKeyAsync(tenantRef, ct).Returns("sk-tenant");
        store.ReadAsync("sk-tenant", ct).Returns("tenant-value");

        var sut = CreateSut(registry, store, policy);

        var resolution = await sut.ResolveWithPathAsync(unitRef, ct);

        resolution.Value.ShouldBeNull();
        resolution.Path.ShouldBe(SecretResolvePath.NotFound);
        resolution.EffectiveRef.ShouldBeNull();

        // The tenant store key must not have been resolved after the deny.
        await registry.DidNotReceive().LookupStoreKeyAsync(tenantRef, ct);
        await store.DidNotReceive().ReadAsync("sk-tenant", ct);
    }

    [Fact]
    public async Task ResolveAsync_UnitDenied_ShortCircuitsBeforeRegistry()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();
        var policy = Substitute.For<ISecretAccessPolicy>();

        policy.IsAuthorizedAsync(SecretAccessAction.Read, SecretScope.Unit, "u1", ct)
            .Returns(false);

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "foo");

        var sut = CreateSut(registry, store, policy);

        var resolution = await sut.ResolveWithPathAsync(unitRef, ct);

        resolution.Path.ShouldBe(SecretResolvePath.NotFound);
        resolution.Value.ShouldBeNull();

        await registry.DidNotReceive().LookupStoreKeyAsync(
            Arg.Any<SecretRef>(), Arg.Any<CancellationToken>());
        await policy.DidNotReceive().IsAuthorizedAsync(
            Arg.Any<SecretAccessAction>(), SecretScope.Tenant, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_InheritDisabled_UnitMissesWithoutTenantLookup()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();
        var policy = Substitute.For<ISecretAccessPolicy>();
        policy.IsAuthorizedAsync(Arg.Any<SecretAccessAction>(), Arg.Any<SecretScope>(), Arg.Any<string>(), ct)
            .Returns(true);

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "shared-token");
        var tenantRef = new SecretRef(SecretScope.Tenant, TenantId, "shared-token");
        registry.LookupStoreKeyAsync(unitRef, ct).Returns((string?)null);
        registry.LookupStoreKeyAsync(tenantRef, ct).Returns("sk-tenant");
        store.ReadAsync("sk-tenant", ct).Returns("tenant-value");

        var sut = CreateSut(registry, store, policy, inheritTenantFromUnit: false);

        var resolution = await sut.ResolveWithPathAsync(unitRef, ct);

        resolution.Path.ShouldBe(SecretResolvePath.NotFound);
        resolution.Value.ShouldBeNull();

        // Tenant scope must not be consulted under strict-isolation config.
        await registry.DidNotReceive().LookupStoreKeyAsync(tenantRef, ct);
        await policy.DidNotReceive().IsAuthorizedAsync(
            Arg.Any<SecretAccessAction>(), SecretScope.Tenant, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_TenantScopeRequest_DoesNotTriggerInheritance()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();

        // A direct tenant request that misses should simply return NotFound;
        // there is no cross-scope inheritance in the opposite direction.
        var tenantRef = new SecretRef(SecretScope.Tenant, TenantId, "missing");
        registry.LookupStoreKeyAsync(tenantRef, ct).Returns((string?)null);

        var sut = CreateSut(registry, store);

        var resolution = await sut.ResolveWithPathAsync(tenantRef, ct);

        resolution.Path.ShouldBe(SecretResolvePath.NotFound);
        resolution.Value.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_PlatformScopeRequest_DoesNotFallThroughToTenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();

        // Platform-scope requests never inherit from tenant — platform
        // keys are infra-only.
        var platformRef = new SecretRef(SecretScope.Platform, "platform", "infra-key");
        registry.LookupStoreKeyAsync(platformRef, ct).Returns((string?)null);

        var sut = CreateSut(registry, store);

        var resolution = await sut.ResolveWithPathAsync(platformRef, ct);

        resolution.Path.ShouldBe(SecretResolvePath.NotFound);
        await registry.DidNotReceive().LookupStoreKeyAsync(
            Arg.Is<SecretRef>(r => r.Scope == SecretScope.Tenant), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentCalls_AllReturnConsistentValues()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "foo");
        registry.LookupStoreKeyAsync(unitRef, ct).Returns("sk-unit");
        store.ReadAsync("sk-unit", ct).Returns("unit-value");

        var sut = CreateSut(registry, store);

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => sut.ResolveAsync(unitRef, ct))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(v => v == "unit-value");
    }

    [Fact]
    public async Task ListAsync_DelegatesToRegistry()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();

        var expected = new List<SecretRef>
        {
            new(SecretScope.Unit, "u1", "a"),
            new(SecretScope.Unit, "u1", "b"),
        };
        registry.ListAsync(SecretScope.Unit, "u1", ct).Returns(expected);

        var sut = CreateSut(registry, store);

        var result = await sut.ListAsync(SecretScope.Unit, "u1", ct);

        result.ShouldBe(expected);
    }
}