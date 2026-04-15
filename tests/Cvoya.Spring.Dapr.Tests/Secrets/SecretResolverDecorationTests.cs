// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Secrets;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Secrets;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Locks down the DI decoration contract for <see cref="ISecretResolver"/>
/// and <see cref="ISecretRegistry"/> (#202). The audit-log decorator
/// lives in the private cloud repo; this OSS test substitutes a fake
/// decorator that records the exact call shape a real audit layer
/// would see, and asserts:
///
/// <list type="bullet">
///   <item>Registering a decorator AFTER the baseline resolver registration routes calls through the decorator.</item>
///   <item>A re-registration of the baseline via <c>TryAddScoped</c> does NOT overwrite the decorator (idempotent re-registration).</item>
///   <item>The decorator observes the <see cref="SecretRef"/>, tenant context, and the inner <see cref="SecretResolution"/> (path, effective ref, version).</item>
///   <item>The parallel decoration hook on <see cref="ISecretRegistry"/> composes — rotation events are observable by an audit wrapper.</item>
/// </list>
///
/// <para>
/// Uses a minimal hand-built service collection rather than invoking
/// <c>AddCvoyaSpringDapr</c> so the assertions stay focused on the
/// decoration contract, independent of the full Dapr composition (which
/// drags in hosted services, a live DaprClient, and so on). The
/// separate DI-composition tests in
/// <c>ServiceCollectionExtensionsTests</c> cover the full wiring.
/// </para>
///
/// <para>
/// Future OSS changes that break the decoration pattern (e.g. making
/// the resolver registration non-TryAdd, or changing the call shape
/// of <see cref="SecretResolution"/>) will fail these tests before
/// they leak into the private cloud.
/// </para>
/// </summary>
public class SecretResolverDecorationTests
{
    private const string KnownTenantId = "audit-test-tenant";

    [Fact]
    public void DecoratingSecretResolver_AfterBaselineRegistration_RoutesCallsThroughDecorator()
    {
        var services = BuildMinimalSecretsServices();

        // Baseline registration (what AddCvoyaSpringDapr does):
        services.TryAddScoped<ISecretResolver, ComposedSecretResolver>();

        // Decorator registration AFTER the baseline — the pattern
        // recommended in ISecretResolver's XML doc.
        var recordings = new List<ResolverCall>();
        services.AddScoped<ComposedSecretResolver>();
        services.Replace(ServiceDescriptor.Scoped<ISecretResolver>(
            sp => new RecordingResolverDecorator(
                inner: sp.GetRequiredService<ComposedSecretResolver>(),
                recordings: recordings,
                tenantContext: sp.GetRequiredService<ITenantContext>())));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<ISecretResolver>();

        // Assert: DI returns the decorator, not the raw composed resolver.
        resolver.ShouldBeOfType<RecordingResolverDecorator>();
    }

    [Fact]
    public async Task DecoratorObservesExpectedCallShape_RefTenantAndResolution()
    {
        var services = BuildMinimalSecretsServices();

        // Control the registry/store so the inner resolver produces a
        // deterministic SecretResolution the decorator can observe.
        var registry = Substitute.For<ISecretRegistry>();
        var store = Substitute.For<ISecretStore>();
        services.Replace(ServiceDescriptor.Scoped(_ => registry));
        services.Replace(ServiceDescriptor.Singleton(_ => store));

        var unitRef = new SecretRef(SecretScope.Unit, "u1", "gh-token");
        registry.LookupWithVersionAsync(unitRef, Arg.Any<CancellationToken>())
            .Returns((new SecretPointer("sk-opaque", SecretOrigin.PlatformOwned), (int?)4));
        store.ReadAsync("sk-opaque", Arg.Any<CancellationToken>())
            .Returns("plaintext");

        services.TryAddScoped<ISecretResolver, ComposedSecretResolver>();
        services.AddScoped<ComposedSecretResolver>();
        var recordings = new List<ResolverCall>();
        services.Replace(ServiceDescriptor.Scoped<ISecretResolver>(
            sp => new RecordingResolverDecorator(
                inner: sp.GetRequiredService<ComposedSecretResolver>(),
                recordings: recordings,
                tenantContext: sp.GetRequiredService<ITenantContext>())));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var ct = TestContext.Current.CancellationToken;

        var resolution = await scope.ServiceProvider
            .GetRequiredService<ISecretResolver>()
            .ResolveWithPathAsync(unitRef, ct);

        // The decorator observed everything a real audit-log layer
        // needs — the requested ref, the tenant in effect, and the
        // full SecretResolution (including Version).
        recordings.Count.ShouldBe(1);
        var call = recordings[0];
        call.Ref.ShouldBe(unitRef);
        call.TenantId.ShouldBe(KnownTenantId);
        call.Resolution.Path.ShouldBe(SecretResolvePath.Direct);
        call.Resolution.EffectiveRef.ShouldBe(unitRef);
        call.Resolution.Version.ShouldBe(4);
        // The decorator HAS access to the plaintext in the inner
        // resolution (it chooses not to log it; see
        // docs/developer/secret-audit.md for the guidance).
        call.Resolution.Value.ShouldBe("plaintext");

        // The outer return to the caller matches.
        resolution.Value.ShouldBe("plaintext");
        resolution.Version.ShouldBe(4);
    }

    [Fact]
    public void BaselineReRegistration_ViaTryAdd_DoesNotOverwriteDecorator()
    {
        // Mirrors the real concern: AddCvoyaSpringDapr uses TryAddScoped
        // for ISecretResolver, so a decorator registered BEFORE a
        // second AddCvoyaSpringDapr call must survive. We simulate
        // the idempotent re-registration with a plain TryAddScoped.
        var services = BuildMinimalSecretsServices();
        services.TryAddScoped<ISecretResolver, ComposedSecretResolver>();

        services.AddScoped<ComposedSecretResolver>();
        var recordings = new List<ResolverCall>();
        services.Replace(ServiceDescriptor.Scoped<ISecretResolver>(
            sp => new RecordingResolverDecorator(
                inner: sp.GetRequiredService<ComposedSecretResolver>(),
                recordings: recordings,
                tenantContext: sp.GetRequiredService<ITenantContext>())));

        // Re-run the baseline registration — TryAddScoped is a no-op
        // because a registration already exists.
        services.TryAddScoped<ISecretResolver, ComposedSecretResolver>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolver = scope.ServiceProvider.GetRequiredService<ISecretResolver>();
        resolver.ShouldBeOfType<RecordingResolverDecorator>();
    }

    [Fact]
    public async Task RegistryDecoration_ObservesRotationEventShape()
    {
        // ISecretRegistry supports the same decoration pattern. An
        // audit-log layer wrapping the registry observes rotations
        // directly: ref, from/to versions, pointer transition. This
        // test locks down that shape so future changes are caught.
        var services = BuildMinimalSecretsServices();
        services.TryAddScoped<ISecretRegistry, EfSecretRegistry>();

        var rotationEvents = new List<SecretRotation>();
        services.AddScoped<EfSecretRegistry>();
        services.Replace(ServiceDescriptor.Scoped<ISecretRegistry>(
            sp => new RecordingRegistryDecorator(
                inner: sp.GetRequiredService<EfSecretRegistry>(),
                rotations: rotationEvents)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var ct = TestContext.Current.CancellationToken;

        var registry = scope.ServiceProvider.GetRequiredService<ISecretRegistry>();
        registry.ShouldBeOfType<RecordingRegistryDecorator>();

        var @ref = new SecretRef(SecretScope.Unit, "u1", "token");
        await registry.RegisterAsync(@ref, "sk-1", SecretOrigin.PlatformOwned, ct);

        var deleted = new List<string>();
        await registry.RotateAsync(
            @ref,
            "sk-2",
            SecretOrigin.PlatformOwned,
            (key, _) => { deleted.Add(key); return Task.CompletedTask; },
            ct);

        rotationEvents.Count.ShouldBe(1);
        var rot = rotationEvents[0];
        rot.Ref.ShouldBe(@ref);
        rot.FromVersion.ShouldBe(1);
        rot.ToVersion.ShouldBe(2);
        rot.PreviousPointer.StoreKey.ShouldBe("sk-1");
        rot.NewPointer.StoreKey.ShouldBe("sk-2");
        // A5 multi-version: rotate APPENDS and retains the prior slot.
        // The delegate is signature-compat but never invoked; the flag
        // is always false under the multi-version policy.
        rot.PreviousStoreKeyDeleted.ShouldBeFalse();
        deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task RegistryDecoration_ObservesRegisterAndPruneOperations()
    {
        // A5: the audit-decorator contract extends to Register and
        // Prune. An audit wrapper must see every version-lifecycle op
        // through the same single interface — otherwise rotations that
        // are actually "POST-then-prune" would slip past the audit log.
        var services = BuildMinimalSecretsServices();
        services.TryAddScoped<ISecretRegistry, EfSecretRegistry>();

        var rotations = new List<SecretRotation>();
        var prunes = new List<PruneObservation>();
        var registers = new List<RegisterObservation>();
        services.AddScoped<EfSecretRegistry>();
        services.Replace(ServiceDescriptor.Scoped<ISecretRegistry>(
            sp => new RecordingRegistryDecorator(
                inner: sp.GetRequiredService<EfSecretRegistry>(),
                rotations: rotations,
                prunes: prunes,
                registers: registers)));

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var ct = TestContext.Current.CancellationToken;

        var registry = scope.ServiceProvider.GetRequiredService<ISecretRegistry>();
        var @ref = new SecretRef(SecretScope.Unit, "u1", "token");

        await registry.RegisterAsync(@ref, "sk-1", SecretOrigin.PlatformOwned, ct);
        await registry.RotateAsync(@ref, "sk-2", SecretOrigin.PlatformOwned, null, ct);
        await registry.RotateAsync(@ref, "sk-3", SecretOrigin.PlatformOwned, null, ct);
        var pruned = await registry.PruneAsync(@ref, keep: 1, null, ct);

        registers.Count.ShouldBe(1);
        registers[0].Ref.ShouldBe(@ref);
        registers[0].Origin.ShouldBe(SecretOrigin.PlatformOwned);

        rotations.Count.ShouldBe(2);
        rotations[0].ToVersion.ShouldBe(2);
        rotations[1].ToVersion.ShouldBe(3);

        prunes.Count.ShouldBe(1);
        prunes[0].Keep.ShouldBe(1);
        prunes[0].Pruned.ShouldBe(2);
        pruned.ShouldBe(2);
    }

    // ------------------------------------------------------------------
    // Test fixtures
    // ------------------------------------------------------------------

    private static ServiceCollection BuildMinimalSecretsServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Tenant context bound to a known id so the decorator recording
        // can assert the tenant in effect.
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.CurrentTenantId.Returns(KnownTenantId);
        services.AddSingleton(tenantContext);

        // Minimal SecretsOptions — the resolver consults
        // InheritTenantFromUnit but we don't rely on fall-through here.
        services.AddSingleton<IOptions<SecretsOptions>>(
            Options.Create(new SecretsOptions { InheritTenantFromUnit = true }));

        // Default dependencies. Override as needed per test.
        services.AddSingleton<ISecretAccessPolicy, AllowAllSecretAccessPolicy>();
        services.AddSingleton(Substitute.For<ISecretStore>());

        // In-memory EF database — only used by the registry-decoration
        // test which actually exercises EfSecretRegistry; other tests
        // replace ISecretRegistry with a substitute.
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase($"DecoratorTest_{Guid.NewGuid():N}"));
        services.AddScoped<ISecretRegistry, EfSecretRegistry>();

        return services;
    }

    private record ResolverCall(SecretRef Ref, string TenantId, SecretResolution Resolution);

    /// <summary>
    /// Minimal resolver decorator — the call-shape stand-in for the
    /// private cloud's audit-log layer.
    /// </summary>
    private class RecordingResolverDecorator : ISecretResolver
    {
        private readonly ISecretResolver _inner;
        private readonly List<ResolverCall> _recordings;
        private readonly ITenantContext _tenantContext;

        public RecordingResolverDecorator(
            ISecretResolver inner,
            List<ResolverCall> recordings,
            ITenantContext tenantContext)
        {
            _inner = inner;
            _recordings = recordings;
            _tenantContext = tenantContext;
        }

        public Task<string?> ResolveAsync(SecretRef @ref, CancellationToken ct)
            => _inner.ResolveAsync(@ref, ct);

        public async Task<SecretResolution> ResolveWithPathAsync(SecretRef @ref, CancellationToken ct)
        {
            var resolution = await _inner.ResolveWithPathAsync(@ref, ct);
            _recordings.Add(new ResolverCall(@ref, _tenantContext.CurrentTenantId, resolution));
            return resolution;
        }

        public async Task<SecretResolution> ResolveWithPathAsync(SecretRef @ref, int? version, CancellationToken ct)
        {
            // Version-aware decorator surface: the audit layer sees the
            // version pin in addition to the ref, so it can record
            // "caller asked for v2 specifically" events separately from
            // latest-only reads.
            var resolution = await _inner.ResolveWithPathAsync(@ref, version, ct);
            _recordings.Add(new ResolverCall(@ref, _tenantContext.CurrentTenantId, resolution));
            return resolution;
        }

        public Task<IReadOnlyList<SecretRef>> ListAsync(SecretScope scope, string ownerId, CancellationToken ct)
            => _inner.ListAsync(scope, ownerId, ct);
    }

    /// <summary>
    /// Minimal registry decorator — stand-in for a private-cloud audit
    /// layer that wraps <see cref="ISecretRegistry"/> to observe
    /// rotation events.
    /// </summary>
    private class RecordingRegistryDecorator : ISecretRegistry
    {
        private readonly ISecretRegistry _inner;
        private readonly List<SecretRotation> _rotations;
        private readonly List<PruneObservation>? _prunes;
        private readonly List<RegisterObservation>? _registers;

        public RecordingRegistryDecorator(
            ISecretRegistry inner,
            List<SecretRotation> rotations,
            List<PruneObservation>? prunes = null,
            List<RegisterObservation>? registers = null)
        {
            _inner = inner;
            _rotations = rotations;
            _prunes = prunes;
            _registers = registers;
        }

        public async Task RegisterAsync(SecretRef @ref, string storeKey, SecretOrigin origin, CancellationToken ct)
        {
            await _inner.RegisterAsync(@ref, storeKey, origin, ct);
            _registers?.Add(new RegisterObservation(@ref, origin));
        }

        public Task<SecretPointer?> LookupAsync(SecretRef @ref, CancellationToken ct)
            => _inner.LookupAsync(@ref, ct);

        public Task<string?> LookupStoreKeyAsync(SecretRef @ref, CancellationToken ct)
            => _inner.LookupStoreKeyAsync(@ref, ct);

        public Task<(SecretPointer Pointer, int? Version)?> LookupWithVersionAsync(SecretRef @ref, CancellationToken ct)
            => _inner.LookupWithVersionAsync(@ref, ct);

        public Task<(SecretPointer Pointer, int? Version)?> LookupWithVersionAsync(SecretRef @ref, int? version, CancellationToken ct)
            => _inner.LookupWithVersionAsync(@ref, version, ct);

        public Task<IReadOnlyList<SecretRef>> ListAsync(SecretScope scope, string ownerId, CancellationToken ct)
            => _inner.ListAsync(scope, ownerId, ct);

        public Task<IReadOnlyList<SecretVersionInfo>> ListVersionsAsync(SecretRef @ref, CancellationToken ct)
            => _inner.ListVersionsAsync(@ref, ct);

        public async Task<SecretRotation> RotateAsync(
            SecretRef @ref,
            string newStoreKey,
            SecretOrigin newOrigin,
            Func<string, CancellationToken, Task>? deletePreviousStoreKeyAsync,
            CancellationToken ct)
        {
            var rotation = await _inner.RotateAsync(@ref, newStoreKey, newOrigin, deletePreviousStoreKeyAsync, ct);
            _rotations.Add(rotation);
            return rotation;
        }

        public async Task<int> PruneAsync(
            SecretRef @ref,
            int keep,
            Func<string, CancellationToken, Task>? deletePrunedStoreKeyAsync,
            CancellationToken ct)
        {
            var pruned = await _inner.PruneAsync(@ref, keep, deletePrunedStoreKeyAsync, ct);
            _prunes?.Add(new PruneObservation(@ref, keep, pruned));
            return pruned;
        }

        public Task DeleteAsync(SecretRef @ref, CancellationToken ct)
            => _inner.DeleteAsync(@ref, ct);
    }

    internal record PruneObservation(SecretRef Ref, int Keep, int Pruned);

    internal record RegisterObservation(SecretRef Ref, SecretOrigin Origin);
}