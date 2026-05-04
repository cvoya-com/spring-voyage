// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Cloning;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Cloning;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DefaultAgentCloningPolicyEnforcer"/>. Each test
/// exercises one dimension of the enforcement chain (allowed-policy,
/// allowed-attachment, depth cap, boundary opacity) with the other axes
/// left unconstrained, so a failure localises to the dimension under test.
/// </summary>
public class DefaultAgentCloningPolicyEnforcerTests
{
    // Stable UUID for the "ada" agent used in boundary tests.
    private static readonly Guid AgentAdaUuid = new("aadaadaa-0000-0000-0000-000000000001");
    private static readonly Guid UnitResearchCellUuid = new("cccccccc-0000-0000-0000-000000000001");

    private readonly InMemoryStateStore _stateStore = new();
    private readonly StateStoreAgentCloningPolicyRepository _repository;
    private readonly IUnitMembershipRepository _membershipRepository = Substitute.For<IUnitMembershipRepository>();
    private readonly IUnitBoundaryStore _boundaryStore = Substitute.For<IUnitBoundaryStore>();
    private readonly ITenantContext _tenantContext = Substitute.For<ITenantContext>();
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly DefaultAgentCloningPolicyEnforcer _sut;

    public DefaultAgentCloningPolicyEnforcerTests()
    {
        _repository = new StateStoreAgentCloningPolicyRepository(_stateStore);
        _tenantContext.CurrentTenantId.Returns(new Guid("aaaaaaaa-1111-1111-1111-000000000001"));

        _membershipRepository
            .ListByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UnitMembership>());

        // By default the directory returns no entries (no units visible).
        _directoryService.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DirectoryEntry>());

        _sut = new DefaultAgentCloningPolicyEnforcer(
            _repository,
            _tenantContext,
            _membershipRepository,
            _boundaryStore,
            _stateStore,
            _directoryService,
            NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task EvaluateAsync_NoPoliciesPersisted_Allows()
    {
        var decision = await _sut.EvaluateAsync(
            "ada", CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached,
            TestContext.Current.CancellationToken);

        decision.Allowed.ShouldBeTrue();
        decision.ResolvedMaxClones.ShouldBeNull();
        decision.ResolvedBudget.ShouldBeNull();
    }

    [Fact]
    public async Task EvaluateAsync_AllowedPoliciesOmitsRequest_Denies()
    {
        await _repository.SetAsync(
            CloningPolicyScope.Agent, "ada",
            new AgentCloningPolicy(AllowedPolicies: new[] { CloningPolicy.EphemeralNoMemory }),
            TestContext.Current.CancellationToken);

        var decision = await _sut.EvaluateAsync(
            "ada", CloningPolicy.EphemeralWithMemory, AttachmentMode.Detached,
            TestContext.Current.CancellationToken);

        decision.Allowed.ShouldBeFalse();
        decision.DeniedDimension.ShouldBe("policy");
    }

    [Fact]
    public async Task EvaluateAsync_AllowedAttachmentOmitsRequest_Denies()
    {
        await _repository.SetAsync(
            CloningPolicyScope.Agent, "ada",
            new AgentCloningPolicy(AllowedAttachmentModes: new[] { AttachmentMode.Attached }),
            TestContext.Current.CancellationToken);

        var decision = await _sut.EvaluateAsync(
            "ada", CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached,
            TestContext.Current.CancellationToken);

        decision.Allowed.ShouldBeFalse();
        decision.DeniedDimension.ShouldBe("attachment");
    }

    [Fact]
    public async Task EvaluateAsync_TenantCapTighterThanAgentCap_TenantWins()
    {
        await _repository.SetAsync(
            CloningPolicyScope.Agent, "ada",
            new AgentCloningPolicy(MaxClones: 10),
            TestContext.Current.CancellationToken);
        await _repository.SetAsync(
            CloningPolicyScope.Tenant, _tenantContext.CurrentTenantId.ToString("N"),
            new AgentCloningPolicy(MaxClones: 3),
            TestContext.Current.CancellationToken);

        var decision = await _sut.EvaluateAsync(
            "ada", CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached,
            TestContext.Current.CancellationToken);

        decision.Allowed.ShouldBeTrue();
        decision.ResolvedMaxClones.ShouldBe(3);
    }

    [Fact]
    public async Task EvaluateAsync_MaxDepthZero_DeniesRootClone()
    {
        // MaxDepth = 0 means "no recursive cloning at all" — applies even to
        // a first-level clone, since cloning a depth-0 source produces a
        // depth-1 clone which already exceeds the cap.
        await _repository.SetAsync(
            CloningPolicyScope.Agent, "ada",
            new AgentCloningPolicy(MaxDepth: 0),
            TestContext.Current.CancellationToken);

        var decision = await _sut.EvaluateAsync(
            "ada", CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached,
            TestContext.Current.CancellationToken);

        decision.Allowed.ShouldBeFalse();
        decision.DeniedDimension.ShouldBe("max-depth");
    }

    [Fact]
    public async Task EvaluateAsync_MaxDepthOne_AllowsRootClone_DeniesRecursive()
    {
        await _repository.SetAsync(
            CloningPolicyScope.Agent, "ada",
            new AgentCloningPolicy(MaxDepth: 1),
            TestContext.Current.CancellationToken);

        // Source 'ada' is a root agent — depth walk returns 0, new clone at 1.
        var rootDecision = await _sut.EvaluateAsync(
            "ada", CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached,
            TestContext.Current.CancellationToken);
        rootDecision.Allowed.ShouldBeTrue();

        // Persist a CloneIdentity making 'ada-clone-1' look like a depth-1 clone.
        await _stateStore.SetAsync(
            $"ada-clone-1:{StateKeys.CloneIdentity}",
            new CloneIdentity("ada", "ada-clone-1", CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached),
            TestContext.Current.CancellationToken);

        await _repository.SetAsync(
            CloningPolicyScope.Agent, "ada-clone-1",
            new AgentCloningPolicy(MaxDepth: 1),
            TestContext.Current.CancellationToken);

        var recursiveDecision = await _sut.EvaluateAsync(
            "ada-clone-1", CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached,
            TestContext.Current.CancellationToken);
        recursiveDecision.Allowed.ShouldBeFalse();
        recursiveDecision.DeniedDimension.ShouldBe("max-depth");
    }

    [Fact]
    public async Task EvaluateAsync_OpaqueBoundary_DeniesDetachedAttachment()
    {
        var unitAddr = Address.For("unit", TestSlugIds.HexFor("research-cell"));

        _membershipRepository
            .ListByAgentAsync(AgentAdaUuid, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new UnitMembership(UnitResearchCellUuid, AgentAdaUuid),
            });

        // Directory must expose the unit entry so the enforcer can resolve the UUID → address.
        _directoryService.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new DirectoryEntry(unitAddr, UnitResearchCellUuid, "research-cell", string.Empty, null, DateTimeOffset.UtcNow),
            });

        _boundaryStore
            .GetAsync(unitAddr, Arg.Any<CancellationToken>())
            .Returns(new UnitBoundary(
                Opacities: new[] { new BoundaryOpacityRule("*", null) }));

        var decision = await _sut.EvaluateAsync(
            AgentAdaUuid.ToString(), CloningPolicy.EphemeralNoMemory, AttachmentMode.Detached,
            TestContext.Current.CancellationToken);

        decision.Allowed.ShouldBeFalse();
        decision.DeniedDimension.ShouldBe("boundary");
    }

    [Fact]
    public async Task EvaluateAsync_OpaqueBoundary_AllowsAttachedAttachment()
    {
        // Attached clones roll up inside the parent's boundary, so the
        // opacity rule does not surface them to the outside.
        var unitAddr = Address.For("unit", TestSlugIds.HexFor("research-cell"));

        _membershipRepository
            .ListByAgentAsync(AgentAdaUuid, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new UnitMembership(UnitResearchCellUuid, AgentAdaUuid),
            });

        _directoryService.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new DirectoryEntry(unitAddr, UnitResearchCellUuid, "research-cell", string.Empty, null, DateTimeOffset.UtcNow),
            });

        _boundaryStore
            .GetAsync(unitAddr, Arg.Any<CancellationToken>())
            .Returns(new UnitBoundary(
                Opacities: new[] { new BoundaryOpacityRule("*", null) }));

        var decision = await _sut.EvaluateAsync(
            AgentAdaUuid.ToString(), CloningPolicy.EphemeralNoMemory, AttachmentMode.Attached,
            TestContext.Current.CancellationToken);

        decision.Allowed.ShouldBeTrue();
    }

    /// <summary>
    /// JSON-round-tripping in-memory IStateStore — mirrors the Dapr state
    /// store's serialisation behaviour so the enforcer's depth walk sees
    /// identical bytes to production.
    /// </summary>
    private sealed class InMemoryStateStore : IStateStore
    {
        private readonly ConcurrentDictionary<string, string> _store = new();

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            if (_store.TryGetValue(key, out var json))
            {
                return Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<T>(json));
            }
            return Task.FromResult<T?>(default);
        }

        public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
        {
            _store[key] = System.Text.Json.JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            _store.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<bool> ContainsAsync(string key, CancellationToken ct = default)
            => Task.FromResult(_store.ContainsKey(key));
    }
}