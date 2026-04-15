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
        registry.LookupWithVersionAsync(unitRef, ct)
            .Returns((new SecretPointer("sk-unit", SecretOrigin.PlatformOwned), (int?)3));
        store.ReadAsync("sk-unit", ct).Returns("unit-value");

        var sut = CreateSut(registry, store, policy);

        var result = await sut.ResolveAsync(unitRef, ct);

        result.ShouldBe("unit-value");

        // Tenant scope must not be consulted on a direct hit.
        await registry.DidNotReceive().LookupWithVersionAsync(
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
        registry.LookupWithVersionAsync(unitRef, ct)
            .Returns((new SecretPointer("sk-unit", SecretOrigin.PlatformOwned), (int?)7));
        store.ReadAsync("sk-unit", ct).Returns("unit-value");

        var sut = CreateSut(registry, store);

        var resolution = await sut.ResolveWithPathAsync(unitRef, ct);

        resolution.Value.ShouldBe("unit-value");
        resolution.Path.ShouldBe(SecretResolvePath.Direct);
        resolution.EffectiveRef.ShouldBe(unitRef);
        // Version flows through from the registry to the audit-visible
        // SecretResolution so decorators can record which version was
        // served (see #201 / #202).
        resolution.Version.ShouldBe(7);
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
        registry.LookupWithVersionAsync(unitRef, ct)
            .Returns(((SecretPointer Pointer, int? Version)?)null);
        registry.LookupWithVersionAsync(tenantRef, ct)
            .Returns((new SecretPointer("sk-tenant", SecretOrigin.PlatformOwned), (int?)2));
        store.ReadAsync("sk-tenant", ct).Returns("tenant-value");

        var sut = CreateSut(registry, store, policy);

        var resolution = await sut.ResolveWithPathAsync(unitRef, ct);

        resolution.Value.ShouldBe("tenant-value");
        resolution.Path.ShouldBe(SecretResolvePath.InheritedFromTenant);
        resolution.EffectiveRef.ShouldBe(tenantRef);
        resolution.Version.ShouldBe(2);

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
        registry.LookupWithVersionAsync(
            Arg.Any<SecretRef>(), Arg.Any<CancellationToken>())
            .Returns(((SecretPointer Pointer, int? Version)?)null);

        var sut = CreateSut(registry, store);

        var resolution = await sut.ResolveWithPathAsync(unitRef, ct);

        resolution.Value.ShouldBeNull();
        resolution.Path.ShouldBe(SecretResolvePath.NotFound);
        resolution.EffectiveRef.ShouldBeNull();
        resolution.Version.ShouldBeNull();

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
        registry.LookupWithVersionAsync(unitRef, ct)
            .Returns(((SecretPointer Pointer, int? Version)?)null);
        registry.LookupWithVersionAsync(tenantRef, ct)
            .Returns((new SecretPointer("sk-tenant", SecretOrigin.PlatformOwned), (int?)1));
        store.ReadAsync("sk-tenant", ct).Returns("tenant-value");

        var sut = CreateSut(registry, store, policy);

        var resolution = await sut.ResolveWithPathAsync(unitRef, ct);

        resolution.Value.ShouldBeNull();
        resolution.Path.ShouldBe(SecretResolvePath.NotFound);
        resolution.EffectiveRef.ShouldBeNull();

        // The tenant store key must not have been resolved after the deny.
        await registry.DidNotReceive().LookupWithVersionAsync(tenantRef, ct);
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

        await registry.DidNotReceive().LookupWithVersionAsync(
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
        registry.LookupWithVersionAsync(unitRef, ct)
            .Returns(((SecretPointer Pointer, int? Version)?)null);
        registry.LookupWithVersionAsync(tenantRef, ct)
            .Returns((new SecretPointer("sk-tenant", SecretOrigin.PlatformOwned), (int?)1));
        store.ReadAsync("sk-tenant", ct).Returns("tenant-value");

        var sut = CreateSut(registry, store, policy, inheritTenantFromUnit: false);

        var resolution = await sut.ResolveWithPathAsync(unitRef, ct);

        resolution.Path.ShouldBe(SecretResolvePath.NotFound);
        resolution.Value.ShouldBeNull();

        // Tenant scope must not be consulted under strict-isolation config.
        await registry.DidNotReceive().LookupWithVersionAsync(tenantRef, ct);
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
        registry.LookupWithVersionAsync(tenantRef, ct)
            .Returns(((SecretPointer Pointer, int? Version)?)null);

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
        registry.LookupWithVersionAsync(platformRef, ct)
            .Returns(((SecretPointer Pointer, int? Version)?)null);

        var sut = CreateSut(registry, store);

        var resolution = await sut.ResolveWithPathAsync(platformRef, ct);

        resolution.Path.ShouldBe(SecretResolvePath.NotFound);
        await registry.DidNotReceive().LookupWithVersionAsync(
            Arg.Is<SecretRef>(r => r.Scope == SecretScope.Tenant), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentCalls_AllReturnConsistentValues()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "foo");
        registry.LookupWithVersionAsync(unitRef, ct)
            .Returns((new SecretPointer("sk-unit", SecretOrigin.PlatformOwned), (int?)1));
        store.ReadAsync("sk-unit", ct).Returns("unit-value");

        var sut = CreateSut(registry, store);

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => sut.ResolveAsync(unitRef, ct))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        results.ShouldAllBe(v => v == "unit-value");
    }

    // ----------------------------------------------------------------------
    // Version pinning (wave 7 A5)
    // ----------------------------------------------------------------------

    [Fact]
    public async Task ResolveWithPathAsync_VersionPinned_UnitHasThatVersion_ReturnsIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "foo");
        registry.LookupWithVersionAsync(unitRef, 2, ct)
            .Returns((new SecretPointer("sk-unit-v2", SecretOrigin.PlatformOwned), (int?)2));
        store.ReadAsync("sk-unit-v2", ct).Returns("value-v2");

        var sut = CreateSut(registry, store);

        var resolution = await sut.ResolveWithPathAsync(unitRef, 2, ct);

        resolution.Value.ShouldBe("value-v2");
        resolution.Path.ShouldBe(SecretResolvePath.Direct);
        resolution.Version.ShouldBe(2);
    }

    [Fact]
    public async Task ResolveWithPathAsync_VersionPinned_NoMatchAtUnit_FallsThroughToSameVersionAtTenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();
        var policy = Substitute.For<ISecretAccessPolicy>();
        policy.IsAuthorizedAsync(Arg.Any<SecretAccessAction>(), Arg.Any<SecretScope>(), Arg.Any<string>(), ct)
            .Returns(true);

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "shared");
        var tenantRef = new SecretRef(SecretScope.Tenant, TenantId, "shared");
        registry.LookupWithVersionAsync(unitRef, 2, ct)
            .Returns(((SecretPointer Pointer, int? Version)?)null);
        registry.LookupWithVersionAsync(tenantRef, 2, ct)
            .Returns((new SecretPointer("sk-tenant-v2", SecretOrigin.PlatformOwned), (int?)2));
        store.ReadAsync("sk-tenant-v2", ct).Returns("tenant-value-v2");

        var sut = CreateSut(registry, store, policy);

        var resolution = await sut.ResolveWithPathAsync(unitRef, 2, ct);

        resolution.Value.ShouldBe("tenant-value-v2");
        resolution.Path.ShouldBe(SecretResolvePath.InheritedFromTenant);
        resolution.EffectiveRef.ShouldBe(tenantRef);
        resolution.Version.ShouldBe(2);
    }

    [Fact]
    public async Task ResolveWithPathAsync_VersionPinned_NoMatchAnywhere_ReturnsNotFound_NotSilentFallback()
    {
        // Critical invariant: a pinned read NEVER silently returns a
        // different version. Unit lacks v3, tenant has only v2 (not v3)
        // — the resolver must return NotFound, not the tenant's v2.
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();
        var policy = Substitute.For<ISecretAccessPolicy>();
        policy.IsAuthorizedAsync(Arg.Any<SecretAccessAction>(), Arg.Any<SecretScope>(), Arg.Any<string>(), ct)
            .Returns(true);

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "shared");
        var tenantRef = new SecretRef(SecretScope.Tenant, TenantId, "shared");
        registry.LookupWithVersionAsync(unitRef, 3, ct)
            .Returns(((SecretPointer Pointer, int? Version)?)null);
        registry.LookupWithVersionAsync(tenantRef, 3, ct)
            .Returns(((SecretPointer Pointer, int? Version)?)null);

        var sut = CreateSut(registry, store, policy);

        var resolution = await sut.ResolveWithPathAsync(unitRef, 3, ct);

        resolution.Value.ShouldBeNull();
        resolution.Path.ShouldBe(SecretResolvePath.NotFound);

        // The store must not have been consulted for the tenant v2 pointer.
        await store.DidNotReceive().ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveWithPathAsync_NullVersion_UsesLatestOverload_AndStubsSeeLegacyCallShape()
    {
        // Verifies the back-compat path: ResolveWithPathAsync(ref, ct)
        // and ResolveWithPathAsync(ref, null, ct) both end up calling
        // the single-argument LookupWithVersionAsync, so audit
        // decorators and test stubs written against the A4-era signature
        // keep working.
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "foo");
        registry.LookupWithVersionAsync(unitRef, ct)
            .Returns((new SecretPointer("sk-latest", SecretOrigin.PlatformOwned), (int?)4));
        store.ReadAsync("sk-latest", ct).Returns("latest-value");

        var sut = CreateSut(registry, store);

        var resolution = await sut.ResolveWithPathAsync(unitRef, null, ct);

        resolution.Value.ShouldBe("latest-value");
        resolution.Version.ShouldBe(4);

        // Only the single-arg overload was called.
        await registry.Received(1).LookupWithVersionAsync(unitRef, ct);
        await registry.DidNotReceive()
            .LookupWithVersionAsync(Arg.Any<SecretRef>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveWithPathAsync_LatestNullVersion_UnitEmpty_TenantMulti_InheritsTenantLatest()
    {
        // Scenario from the A5 spec: unit has no versions, tenant has
        // v1 and v2. A latest-resolve at unit scope falls back to
        // tenant and returns tenant's v2 (latest).
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();
        var policy = Substitute.For<ISecretAccessPolicy>();
        policy.IsAuthorizedAsync(Arg.Any<SecretAccessAction>(), Arg.Any<SecretScope>(), Arg.Any<string>(), ct)
            .Returns(true);

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "shared");
        var tenantRef = new SecretRef(SecretScope.Tenant, TenantId, "shared");

        // Latest (null) lookup — the composed resolver uses the legacy
        // overload for null-version lookups.
        registry.LookupWithVersionAsync(unitRef, ct)
            .Returns(((SecretPointer Pointer, int? Version)?)null);
        registry.LookupWithVersionAsync(tenantRef, ct)
            .Returns((new SecretPointer("sk-tenant-v2", SecretOrigin.PlatformOwned), (int?)2));
        store.ReadAsync("sk-tenant-v2", ct).Returns("tenant-v2-value");

        var sut = CreateSut(registry, store, policy);

        var resolution = await sut.ResolveWithPathAsync(unitRef, ct);

        resolution.Value.ShouldBe("tenant-v2-value");
        resolution.Path.ShouldBe(SecretResolvePath.InheritedFromTenant);
        resolution.Version.ShouldBe(2);
    }

    [Fact]
    public async Task ResolveWithPathAsync_VersionPinned_ReadDenied_FailsClosed()
    {
        // Access policy denial stops the resolve before the registry is
        // hit, even for pinned reads. The decorator path still sees the
        // attempt.
        var ct = TestContext.Current.CancellationToken;
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();
        var policy = Substitute.For<ISecretAccessPolicy>();
        policy.IsAuthorizedAsync(SecretAccessAction.Read, SecretScope.Unit, "u1", ct).Returns(false);

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "foo");
        var sut = CreateSut(registry, store, policy);

        var resolution = await sut.ResolveWithPathAsync(unitRef, 2, ct);

        resolution.Path.ShouldBe(SecretResolvePath.NotFound);
        await registry.DidNotReceive()
            .LookupWithVersionAsync(Arg.Any<SecretRef>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
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