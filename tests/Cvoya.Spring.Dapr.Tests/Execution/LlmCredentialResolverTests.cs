// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="LlmCredentialResolver"/> — the tier-2 credential
/// resolver introduced by #615 and de-providerised in #734. Verifies the
/// unit → tenant resolution order, the registry-driven secret-name
/// lookup, and the fail-clean behaviour when nothing is configured or the
/// runtime declares no credential.
/// </summary>
public class LlmCredentialResolverTests
{
    private static readonly Guid TenantId = new("acacacac-0000-0000-0000-000000000001");
    private static readonly Guid UnitU1 = new("a1a1a1a1-0000-0000-0000-000000000001");

    private static LlmCredentialResolver CreateSut(
        ISecretResolver resolver,
        IAgentRuntimeRegistry registry,
        ISecretRegistry? secretRegistry = null,
        IUnitSubunitMembershipRepository? unitSubunitRepository = null)
    {
        var tenantContext = Substitute.For<ITenantContext>();
        tenantContext.CurrentTenantId.Returns(TenantId);

        // Default to "no rows in any registry / unit-subunit projection"
        // ONLY when the caller did not supply their own mock — tests
        // that wire up specific edges pass their pre-configured stubs
        // and we must NOT overwrite their .Returns(...) here.
        if (secretRegistry is null)
        {
            secretRegistry = Substitute.For<ISecretRegistry>();
            secretRegistry.LookupPropagateAsync(Arg.Any<SecretRef>(), Arg.Any<CancellationToken>())
                .Returns((bool?)null);
        }

        if (unitSubunitRepository is null)
        {
            unitSubunitRepository = Substitute.For<IUnitSubunitMembershipRepository>();
            unitSubunitRepository.ListByChildAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<UnitSubunitMembership>());
        }

        return new LlmCredentialResolver(
            registry,
            resolver,
            secretRegistry,
            unitSubunitRepository,
            tenantContext,
            NullLogger<LlmCredentialResolver>.Instance);
    }

    private static IAgentRuntimeRegistry BuildRegistry(params (string Id, string SecretName)[] runtimes)
    {
        var registry = Substitute.For<IAgentRuntimeRegistry>();
        registry.Get(Arg.Any<string>()).Returns((IAgentRuntime?)null);
        var all = new List<IAgentRuntime>(runtimes.Length);
        foreach (var (id, secretName) in runtimes)
        {
            var runtime = Substitute.For<IAgentRuntime>();
            runtime.Id.Returns(id);
            runtime.CredentialSecretName.Returns(secretName);
            registry.Get(id).Returns(runtime);
            all.Add(runtime);
        }
        registry.All.Returns(all);
        return registry;
    }

    [Fact]
    public async Task ResolveAsync_UnknownProvider_ReturnsNotFound()
    {
        var resolver = Substitute.For<ISecretResolver>();
        var registry = BuildRegistry(("claude", "anthropic-api-key"));
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync("no-such-provider", agentId: null, unitId: null, TestContext.Current.CancellationToken);

        result.Value.ShouldBeNull();
        result.Source.ShouldBe(LlmCredentialSource.NotFound);
        result.SecretName.ShouldBeEmpty();
        await resolver.DidNotReceiveWithAnyArgs().ResolveWithPathAsync(
            default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResolveAsync_RuntimeWithoutCredentialSchema_ReturnsNotFound()
    {
        // Ollama-style runtime — empty CredentialSecretName means "no
        // credential to look up"; the resolver must short-circuit before
        // touching the secret store.
        var resolver = Substitute.For<ISecretResolver>();
        var registry = BuildRegistry(("ollama", string.Empty));
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync("ollama", agentId: null, unitId: null, TestContext.Current.CancellationToken);

        result.Value.ShouldBeNull();
        result.Source.ShouldBe(LlmCredentialSource.NotFound);
        result.SecretName.ShouldBeEmpty();
        await resolver.DidNotReceiveWithAnyArgs().ResolveWithPathAsync(
            default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ResolveAsync_UnitScopedHit_ReturnsUnitSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitU1 && r.Name == "anthropic-api-key"),
                ct)
            .Returns(new SecretResolution("sk-unit", SecretResolvePath.Direct, new SecretRef(SecretScope.Unit, UnitU1, "anthropic-api-key")));
        var registry = BuildRegistry(("claude", "anthropic-api-key"));
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync("claude", agentId: null, unitId: UnitU1, ct);

        result.Value.ShouldBe("sk-unit");
        result.Source.ShouldBe(LlmCredentialSource.Unit);
        result.SecretName.ShouldBe("anthropic-api-key");
    }

    [Fact]
    public async Task ResolveAsync_UnitMissesTenantHas_ReportsTenantSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        var unitRef = new SecretRef(SecretScope.Unit, UnitU1, "openai-api-key");
        var tenantRef = new SecretRef(SecretScope.Tenant, TenantId, "openai-api-key");
        resolver.ResolveWithPathAsync(unitRef, ct)
            .Returns(new SecretResolution(
                "sk-tenant",
                SecretResolvePath.InheritedFromTenant,
                tenantRef));
        var registry = BuildRegistry(("openai", "openai-api-key"));
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync("openai", agentId: null, unitId: UnitU1, ct);

        result.Value.ShouldBe("sk-tenant");
        result.Source.ShouldBe(LlmCredentialSource.Tenant);
        result.SecretName.ShouldBe("openai-api-key");
    }

    [Fact]
    public async Task ResolveAsync_NoUnit_QueriesTenantDirectly()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Tenant && r.OwnerId == TenantId && r.Name == "anthropic-api-key"),
                ct)
            .Returns(new SecretResolution(
                "sk-tenant-default",
                SecretResolvePath.Direct,
                new SecretRef(SecretScope.Tenant, TenantId, "anthropic-api-key")));
        var registry = BuildRegistry(("claude", "anthropic-api-key"));
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync("claude", agentId: null, unitId: null, ct);

        result.Value.ShouldBe("sk-tenant-default");
        result.Source.ShouldBe(LlmCredentialSource.Tenant);
        result.SecretName.ShouldBe("anthropic-api-key");
    }

    [Fact]
    public async Task ResolveAsync_UnitAndTenantUnset_ReturnsNotFoundWithSecretName()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(Arg.Any<SecretRef>(), ct)
            .Returns(new SecretResolution(null, SecretResolvePath.NotFound, null));
        var registry = BuildRegistry(("claude", "anthropic-api-key"));
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync("claude", agentId: null, unitId: UnitU1, ct);

        result.Value.ShouldBeNull();
        result.Source.ShouldBe(LlmCredentialSource.NotFound);
        // Even on NotFound, SecretName is populated so error messages can
        // point operators at the exact secret name they must create.
        result.SecretName.ShouldBe("anthropic-api-key");
    }

    [Fact]
    public async Task ResolveAsync_TenantUnset_NoUnit_ReturnsNotFoundWithSecretName()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(Arg.Any<SecretRef>(), ct)
            .Returns(new SecretResolution(null, SecretResolvePath.NotFound, null));
        var registry = BuildRegistry(("google", "google-api-key"));
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync("google", agentId: null, unitId: null, ct);

        result.Value.ShouldBeNull();
        result.Source.ShouldBe(LlmCredentialSource.NotFound);
        result.SecretName.ShouldBe("google-api-key");
    }

    [Theory]
    [InlineData("claude", "anthropic-api-key")]
    [InlineData("openai", "openai-api-key")]
    [InlineData("google", "google-api-key")]
    [InlineData("ollama", "")]
    public async Task ResolveAsync_ReadsSecretNameFromRegistry(string runtimeId, string declaredSecretName)
    {
        // Drives the end-to-end registry-lookup path for every runtime the
        // OSS platform ships. For runtimes with a real credential name, the
        // tenant-scope secret store is consulted with the exact declared
        // name; for the credential-less Ollama runtime, the resolver must
        // short-circuit before touching the secret store.
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        if (!string.IsNullOrEmpty(declaredSecretName))
        {
            resolver.ResolveWithPathAsync(
                    Arg.Is<SecretRef>(r => r.Scope == SecretScope.Tenant && r.Name == declaredSecretName),
                    ct)
                .Returns(new SecretResolution(
                    "value",
                    SecretResolvePath.Direct,
                    new SecretRef(SecretScope.Tenant, TenantId, declaredSecretName)));
        }
        var registry = BuildRegistry((runtimeId, declaredSecretName));
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync(runtimeId, agentId: null, unitId: null, ct);

        if (string.IsNullOrEmpty(declaredSecretName))
        {
            result.Source.ShouldBe(LlmCredentialSource.NotFound);
            result.SecretName.ShouldBeEmpty();
            await resolver.DidNotReceiveWithAnyArgs().ResolveWithPathAsync(default!, ct);
        }
        else
        {
            result.Value.ShouldBe("value");
            result.Source.ShouldBe(LlmCredentialSource.Tenant);
            result.SecretName.ShouldBe(declaredSecretName);
        }
    }

    [Fact]
    public async Task ResolveAsync_CustomRuntimeSecretName_UsesRegistryValue()
    {
        // Exercises a runtime whose secret name was never in the legacy
        // hard-coded switch (e.g. a private-cloud downstream runtime).
        // The resolver must honour whatever the plugin declares.
        var ct = TestContext.Current.CancellationToken;
        const string customName = "cvoya-bespoke-api-key";
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Name == customName),
                ct)
            .Returns(new SecretResolution(
                "bespoke-value",
                SecretResolvePath.Direct,
                new SecretRef(SecretScope.Tenant, TenantId, customName)));
        var registry = BuildRegistry(("bespoke", customName));
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync("bespoke", agentId: null, unitId: null, ct);

        result.Value.ShouldBe("bespoke-value");
        result.Source.ShouldBe(LlmCredentialSource.Tenant);
        result.SecretName.ShouldBe(customName);
    }

    [Fact]
    public async Task ResolveAsync_TenantScope_UnreadableCipher_ReturnsUnreadable()
    {
        // A slot exists but the encryptor can't decrypt it (e.g. the
        // at-rest AES key rotated). The resolver must not let the domain
        // exception propagate past this seam — callers that only want
        // status (the wizard probe) would otherwise crash with a 500.
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Tenant && r.Name == "anthropic-api-key"),
                ct)
            .Returns(Task.FromException<SecretResolution>(new SecretUnreadableException()));
        var registry = BuildRegistry(("claude", "anthropic-api-key"));
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync("claude", agentId: null, unitId: null, ct);

        result.Value.ShouldBeNull();
        result.Source.ShouldBe(LlmCredentialSource.Unreadable);
        result.SecretName.ShouldBe("anthropic-api-key");
    }

    [Fact]
    public async Task ResolveAsync_UnitScope_UnreadableCipher_ReturnsUnreadable()
    {
        // Same contract at unit scope: the domain exception originating
        // inside the unit (or tenant fall-through) lookup must be caught
        // and surfaced as LlmCredentialSource.Unreadable.
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitU1),
                ct)
            .Returns(Task.FromException<SecretResolution>(new SecretUnreadableException()));
        var registry = BuildRegistry(("claude", "anthropic-api-key"));
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync("claude", agentId: null, unitId: UnitU1, ct);

        result.Value.ShouldBeNull();
        result.Source.ShouldBe(LlmCredentialSource.Unreadable);
        result.SecretName.ShouldBe("anthropic-api-key");
    }

    [Fact]
    public async Task ResolveAsync_RegistryLookupIsCaseInsensitiveViaRegistry()
    {
        // The registry contract is case-insensitive on Id — the resolver
        // just forwards; stubbing Get("CLAUDE") on the registry exercises
        // that path without the resolver having to lowercase.
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(Arg.Any<SecretRef>(), ct)
            .Returns(new SecretResolution(
                "sk",
                SecretResolvePath.Direct,
                new SecretRef(SecretScope.Tenant, TenantId, "anthropic-api-key")));
        var registry = Substitute.For<IAgentRuntimeRegistry>();
        var runtime = Substitute.For<IAgentRuntime>();
        runtime.Id.Returns("claude");
        runtime.CredentialSecretName.Returns("anthropic-api-key");
        registry.Get("CLAUDE").Returns(runtime);
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync("CLAUDE", agentId: null, unitId: null, ct);

        result.SecretName.ShouldBe("anthropic-api-key");
    }

    // ---- #1737: agent → unit → parent-unit → tenant chain ------------------

    private static readonly Guid AgentA1 = new("a9a9a9a9-0000-0000-0000-000000000001");
    private static readonly Guid UnitChild = new("c1c1c1c1-0000-0000-0000-000000000001");
    private static readonly Guid UnitParent = new("c2c2c2c2-0000-0000-0000-000000000002");
    private static readonly Guid UnitGrandparent = new("c3c3c3c3-0000-0000-0000-000000000003");

    [Fact]
    public async Task ResolveAsync_AgentScopedSecret_BeatsUnitAndTenant()
    {
        // #1737: agent-scope wins over every other tier. Even when the
        // unit and tenant both have the secret, the agent-scope hit
        // short-circuits and emits LlmCredentialSource.Agent.
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Agent && r.OwnerId == AgentA1 && r.Name == "anthropic-api-key"),
                ct)
            .Returns(new SecretResolution(
                "sk-agent",
                SecretResolvePath.Direct,
                new SecretRef(SecretScope.Agent, AgentA1, "anthropic-api-key")));
        // Unit and tenant return values too — must NOT be consulted past
        // the agent hit.
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit),
                ct)
            .Returns(new SecretResolution(
                "sk-unit",
                SecretResolvePath.Direct,
                new SecretRef(SecretScope.Unit, UnitU1, "anthropic-api-key")));
        var registry = BuildRegistry(("claude", "anthropic-api-key"));
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync("claude", agentId: AgentA1, unitId: UnitU1, ct);

        result.Value.ShouldBe("sk-agent");
        result.Source.ShouldBe(LlmCredentialSource.Agent);
        result.SecretName.ShouldBe("anthropic-api-key");
    }

    [Fact]
    public async Task ResolveAsync_AgentScopeMisses_FallsThroughToUnit()
    {
        // Agent scope returns NotFound; resolver must continue down to
        // unit and surface the unit hit as LlmCredentialSource.Unit.
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Agent),
                ct)
            .Returns(new SecretResolution(null, SecretResolvePath.NotFound, null));
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitU1),
                ct)
            .Returns(new SecretResolution(
                "sk-unit",
                SecretResolvePath.Direct,
                new SecretRef(SecretScope.Unit, UnitU1, "anthropic-api-key")));
        var registry = BuildRegistry(("claude", "anthropic-api-key"));
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync("claude", agentId: AgentA1, unitId: UnitU1, ct);

        result.Value.ShouldBe("sk-unit");
        result.Source.ShouldBe(LlmCredentialSource.Unit);
    }

    [Fact]
    public async Task ResolveAsync_ParentUnitWithPropagateTrue_BeatsTenant()
    {
        // Child unit has no row; parent unit has a propagating row;
        // tenant has a default. Resolver must walk up to the parent and
        // surface LlmCredentialSource.ParentUnit, not Tenant.
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();

        // Direct unit lookup falls through to tenant (no unit row, has
        // tenant default).
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitChild),
                ct)
            .Returns(new SecretResolution(
                "sk-tenant",
                SecretResolvePath.InheritedFromTenant,
                new SecretRef(SecretScope.Tenant, TenantId, "anthropic-api-key")));

        // Parent-unit Direct lookup returns the propagating value.
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitParent),
                ct)
            .Returns(new SecretResolution(
                "sk-parent",
                SecretResolvePath.Direct,
                new SecretRef(SecretScope.Unit, UnitParent, "anthropic-api-key")));

        var secretRegistry = Substitute.For<ISecretRegistry>();
        secretRegistry.LookupPropagateAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitParent),
                ct)
            .Returns(true);

        var unitSubunit = Substitute.For<IUnitSubunitMembershipRepository>();
        // Child → parent edge.
        unitSubunit.ListByChildAsync(UnitChild, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitSubunitMembership(UnitParent, UnitChild) });
        // Parent has tenant as its (synthetic) parent — i.e. top-level
        // unit. We need ListByChild(parent) to return at least one row
        // (so IsUnitAsync(parent) returns true) but with TenantId as
        // the parent so the walk stops there.
        unitSubunit.ListByChildAsync(UnitParent, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitSubunitMembership(TenantId, UnitParent) });
        unitSubunit.ListByChildAsync(TenantId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UnitSubunitMembership>());

        var registry = BuildRegistry(("claude", "anthropic-api-key"));
        var sut = CreateSut(resolver, registry, secretRegistry, unitSubunit);

        var result = await sut.ResolveAsync("claude", agentId: null, unitId: UnitChild, ct);

        result.Value.ShouldBe("sk-parent");
        result.Source.ShouldBe(LlmCredentialSource.ParentUnit);
    }

    [Fact]
    public async Task ResolveAsync_ParentUnitWithPropagateFalse_FallsThroughToTenant()
    {
        // Parent unit has a row but propagate=false. Resolver must skip
        // it and emit Tenant when the tenant has a value.
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();

        // Child unit lookup falls through to tenant.
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitChild),
                ct)
            .Returns(new SecretResolution(
                "sk-tenant",
                SecretResolvePath.InheritedFromTenant,
                new SecretRef(SecretScope.Tenant, TenantId, "anthropic-api-key")));

        var secretRegistry = Substitute.For<ISecretRegistry>();
        // Parent has the row but it's sealed (propagate=false).
        secretRegistry.LookupPropagateAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitParent),
                ct)
            .Returns(false);

        var unitSubunit = Substitute.For<IUnitSubunitMembershipRepository>();
        unitSubunit.ListByChildAsync(UnitChild, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitSubunitMembership(UnitParent, UnitChild) });
        unitSubunit.ListByChildAsync(UnitParent, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitSubunitMembership(TenantId, UnitParent) });

        var registry = BuildRegistry(("claude", "anthropic-api-key"));
        var sut = CreateSut(resolver, registry, secretRegistry, unitSubunit);

        var result = await sut.ResolveAsync("claude", agentId: null, unitId: UnitChild, ct);

        result.Value.ShouldBe("sk-tenant");
        result.Source.ShouldBe(LlmCredentialSource.Tenant);
        // Parent's resolver path must NOT have been read since
        // propagate=false short-circuits before the SecretResolver
        // consult (#1737 contract).
        await resolver.DidNotReceive().ResolveWithPathAsync(
            Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitParent),
            ct);
    }

    [Fact]
    public async Task ResolveAsync_GrandparentPropagates_HitsAtGrandparent()
    {
        // Two-step parent walk. Child → parent → grandparent. Parent has
        // no row; grandparent has a propagating row. Resolver must keep
        // walking past the empty parent and emit ParentUnit.
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();

        // Child unit hits NotFound on its own row; tenant has nothing.
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitChild),
                ct)
            .Returns(new SecretResolution(null, SecretResolvePath.NotFound, null));

        // Grandparent owns the propagating value.
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitGrandparent),
                ct)
            .Returns(new SecretResolution(
                "sk-grandparent",
                SecretResolvePath.Direct,
                new SecretRef(SecretScope.Unit, UnitGrandparent, "anthropic-api-key")));

        var secretRegistry = Substitute.For<ISecretRegistry>();
        secretRegistry.LookupPropagateAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitParent),
                ct)
            .Returns((bool?)null); // parent has no row
        secretRegistry.LookupPropagateAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitGrandparent),
                ct)
            .Returns(true);

        var unitSubunit = Substitute.For<IUnitSubunitMembershipRepository>();
        unitSubunit.ListByChildAsync(UnitChild, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitSubunitMembership(UnitParent, UnitChild) });
        unitSubunit.ListByChildAsync(UnitParent, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitSubunitMembership(UnitGrandparent, UnitParent) });
        unitSubunit.ListByChildAsync(UnitGrandparent, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitSubunitMembership(TenantId, UnitGrandparent) });

        var registry = BuildRegistry(("claude", "anthropic-api-key"));
        var sut = CreateSut(resolver, registry, secretRegistry, unitSubunit);

        var result = await sut.ResolveAsync("claude", agentId: null, unitId: UnitChild, ct);

        result.Value.ShouldBe("sk-grandparent");
        result.Source.ShouldBe(LlmCredentialSource.ParentUnit);
    }

    [Fact]
    public async Task ResolveAsync_FullChain_AgentBeatsUnitBeatsParentBeatsTenant()
    {
        // End-to-end priority test: simultaneously populate all four
        // tiers and assert the resolver picks Agent. Then progressively
        // remove tiers and re-verify each subsequent tier wins.
        var ct = TestContext.Current.CancellationToken;
        var registry = BuildRegistry(("claude", "anthropic-api-key"));

        // Helper: set up resolvers for each requested combination.
        async Task<LlmCredentialResolution> RunAsync(
            bool agentHas, bool unitHas, bool parentHas, bool tenantHas)
        {
            var resolver = Substitute.For<ISecretResolver>();
            // Tenant fall-through path: when the unit has no row, the
            // composed resolver returns the tenant value via
            // InheritedFromTenant. When tenant ALSO has nothing, return
            // NotFound.
            var unitFallthrough = (unitHas, tenantHas) switch
            {
                (true, _) => new SecretResolution("sk-unit", SecretResolvePath.Direct,
                    new SecretRef(SecretScope.Unit, UnitChild, "anthropic-api-key")),
                (false, true) => new SecretResolution("sk-tenant", SecretResolvePath.InheritedFromTenant,
                    new SecretRef(SecretScope.Tenant, TenantId, "anthropic-api-key")),
                _ => new SecretResolution(null, SecretResolvePath.NotFound, null),
            };
            resolver.ResolveWithPathAsync(
                    Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitChild),
                    ct)
                .Returns(unitFallthrough);
            resolver.ResolveWithPathAsync(
                    Arg.Is<SecretRef>(r => r.Scope == SecretScope.Agent && r.OwnerId == AgentA1),
                    ct)
                .Returns(agentHas
                    ? new SecretResolution("sk-agent", SecretResolvePath.Direct,
                        new SecretRef(SecretScope.Agent, AgentA1, "anthropic-api-key"))
                    : new SecretResolution(null, SecretResolvePath.NotFound, null));
            resolver.ResolveWithPathAsync(
                    Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitParent),
                    ct)
                .Returns(parentHas
                    ? new SecretResolution("sk-parent", SecretResolvePath.Direct,
                        new SecretRef(SecretScope.Unit, UnitParent, "anthropic-api-key"))
                    : new SecretResolution(null, SecretResolvePath.NotFound, null));

            var secretRegistry = Substitute.For<ISecretRegistry>();
            secretRegistry.LookupPropagateAsync(
                    Arg.Is<SecretRef>(r => r.Scope == SecretScope.Unit && r.OwnerId == UnitParent),
                    ct)
                .Returns(parentHas ? true : (bool?)null);

            var unitSubunit = Substitute.For<IUnitSubunitMembershipRepository>();
            unitSubunit.ListByChildAsync(UnitChild, Arg.Any<CancellationToken>())
                .Returns(new[] { new UnitSubunitMembership(UnitParent, UnitChild) });
            unitSubunit.ListByChildAsync(UnitParent, Arg.Any<CancellationToken>())
                .Returns(new[] { new UnitSubunitMembership(TenantId, UnitParent) });

            var sut = CreateSut(resolver, registry, secretRegistry, unitSubunit);
            return await sut.ResolveAsync("claude", agentId: AgentA1, unitId: UnitChild, ct);
        }

        (await RunAsync(true, true, true, true)).Source.ShouldBe(LlmCredentialSource.Agent);
        (await RunAsync(false, true, true, true)).Source.ShouldBe(LlmCredentialSource.Unit);
        (await RunAsync(false, false, true, true)).Source.ShouldBe(LlmCredentialSource.ParentUnit);
        (await RunAsync(false, false, false, true)).Source.ShouldBe(LlmCredentialSource.Tenant);
        (await RunAsync(false, false, false, false)).Source.ShouldBe(LlmCredentialSource.NotFound);
    }

    [Fact]
    public async Task ResolveAsync_AgentUnreadable_ReturnsUnreadable()
    {
        // SecretUnreadableException at the agent tier must surface as
        // Unreadable, mirroring the unit/tenant behaviour.
        var ct = TestContext.Current.CancellationToken;
        var resolver = Substitute.For<ISecretResolver>();
        resolver.ResolveWithPathAsync(
                Arg.Is<SecretRef>(r => r.Scope == SecretScope.Agent && r.OwnerId == AgentA1),
                ct)
            .Returns(Task.FromException<SecretResolution>(new SecretUnreadableException()));
        var registry = BuildRegistry(("claude", "anthropic-api-key"));
        var sut = CreateSut(resolver, registry);

        var result = await sut.ResolveAsync("claude", agentId: AgentA1, unitId: null, ct);

        result.Value.ShouldBeNull();
        result.Source.ShouldBe(LlmCredentialSource.Unreadable);
        result.SecretName.ShouldBe("anthropic-api-key");
    }
}