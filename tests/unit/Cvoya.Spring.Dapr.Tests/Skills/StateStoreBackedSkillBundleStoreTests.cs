// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Collections.Concurrent;
using System.Text.Json;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Skills;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit coverage for <see cref="StateStoreBackedUnitSkillBundleStore"/>
/// and <see cref="StateStoreBackedAgentSkillBundleStore"/> (#2360). The
/// two stores share a base class — the tests are theoried across both
/// to lock the contract on a per-subject keyed JSON-state-store
/// implementation: round-tripping the persisted shape, the
/// re-resolve-on-mutation guarantee, ordered idempotent <c>AddAsync</c>,
/// no-op <c>RemoveAsync</c>, and the differing key prefixes that
/// namespace the unit and agent JSON docs.
/// </summary>
public class StateStoreBackedSkillBundleStoreTests
{
    private static readonly JsonElement EmptySchema = JsonSerializer.SerializeToElement(new { });

    public enum Subject
    {
        Unit,
        Agent,
    }

    /// <summary>
    /// Stub <see cref="ISkillBundleResolver"/> that maps each
    /// <see cref="SkillBundleReference"/> to a deterministic
    /// <see cref="SkillBundle"/> so the tests can assert which body was
    /// persisted.
    /// </summary>
    private sealed class StubResolver : ISkillBundleResolver
    {
        public int CallCount;
        public Func<SkillBundleReference, SkillBundle>? Override;

        public Task<SkillBundle> ResolveAsync(
            SkillBundleReference reference,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref CallCount);
            var bundle = Override is not null
                ? Override(reference)
                : new SkillBundle(
                    PackageName: reference.Package,
                    SkillName: reference.Skill,
                    Prompt: $"prompt::{reference.Package}/{reference.Skill}",
                    RequiredTools: Array.Empty<SkillToolRequirement>());
            return Task.FromResult(bundle);
        }
    }

    /// <summary>
    /// In-memory <see cref="IStateStore"/> for unit tests. Keys the
    /// payload by string so the production key prefix can be observed
    /// from the tests.
    /// </summary>
    private sealed class FakeStateStore : IStateStore
    {
        public readonly ConcurrentDictionary<string, byte[]> Items = new();

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        {
            if (Items.TryGetValue(key, out var bytes))
            {
                var doc = JsonSerializer.Deserialize<T>(bytes);
                return Task.FromResult(doc);
            }
            return Task.FromResult<T?>(default);
        }

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken = default)
        {
            Items[key] = JsonSerializer.SerializeToUtf8Bytes(value);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            Items.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<bool> ContainsAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Items.ContainsKey(key));
    }

    private static (object Store, FakeStateStore State, StubResolver Resolver, string ExpectedKeyPrefix)
        Build(Subject subject)
    {
        var state = new FakeStateStore();
        var resolver = new StubResolver();
        object store = subject == Subject.Unit
            ? new StateStoreBackedUnitSkillBundleStore(state, resolver)
            : new StateStoreBackedAgentSkillBundleStore(state, resolver);
        var prefix = subject == Subject.Unit ? "Unit:SkillBundles:" : "Agent:SkillBundles:";
        return (store, state, resolver, prefix);
    }

    private static Task<IReadOnlyList<SkillBundle>> SetAsync(
        object store, string id, IReadOnlyList<SkillBundleReference> refs, CancellationToken ct) =>
        store switch
        {
            IUnitSkillBundleStore u => u.SetAsync(id, refs, ct),
            IAgentSkillBundleStore a => a.SetAsync(id, refs, ct),
            _ => throw new System.NotSupportedException(),
        };

    private static Task<IReadOnlyList<SkillBundle>> AddAsync(
        object store, string id, SkillBundleReference reference, CancellationToken ct) =>
        store switch
        {
            IUnitSkillBundleStore u => u.AddAsync(id, reference, ct),
            IAgentSkillBundleStore a => a.AddAsync(id, reference, ct),
            _ => throw new System.NotSupportedException(),
        };

    private static Task<IReadOnlyList<SkillBundle>> RemoveAsync(
        object store, string id, string pkg, string skill, CancellationToken ct) =>
        store switch
        {
            IUnitSkillBundleStore u => u.RemoveAsync(id, pkg, skill, ct),
            IAgentSkillBundleStore a => a.RemoveAsync(id, pkg, skill, ct),
            _ => throw new System.NotSupportedException(),
        };

    private static Task<IReadOnlyList<SkillBundle>> GetAsync(
        object store, string id, CancellationToken ct) =>
        store switch
        {
            IUnitSkillBundleStore u => u.GetAsync(id, ct),
            IAgentSkillBundleStore a => a.GetAsync(id, ct),
            _ => throw new System.NotSupportedException(),
        };

    private static Task DeleteAsync(
        object store, string id, CancellationToken ct) =>
        store switch
        {
            IUnitSkillBundleStore u => u.DeleteAsync(id, ct),
            IAgentSkillBundleStore a => a.DeleteAsync(id, ct),
            _ => throw new System.NotSupportedException(),
        };

    [Theory]
    [InlineData(Subject.Unit)]
    [InlineData(Subject.Agent)]
    public async Task GetAsync_EmptyStore_ReturnsEmpty(Subject subject)
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, _, _) = Build(subject);

        var bundles = await GetAsync(store, "subject-1", ct);

        bundles.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Subject.Unit)]
    [InlineData(Subject.Agent)]
    public async Task SetAsync_ResolvesAndPersistsInDeclarationOrder(Subject subject)
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, state, resolver, prefix) = Build(subject);

        var refs = new[]
        {
            new SkillBundleReference("pkg-a", "skill-a"),
            new SkillBundleReference("pkg-b", "skill-b"),
        };

        var written = await SetAsync(store, "subject-1", refs, ct);

        written.Count.ShouldBe(2);
        written[0].PackageName.ShouldBe("pkg-a");
        written[0].SkillName.ShouldBe("skill-a");
        written[0].Prompt.ShouldBe("prompt::pkg-a/skill-a");
        written[1].PackageName.ShouldBe("pkg-b");
        resolver.CallCount.ShouldBe(2);

        // The persisted key carries the correct prefix.
        state.Items.Keys.ShouldContain($"{prefix}subject-1");

        var roundTripped = await GetAsync(store, "subject-1", ct);
        roundTripped.Count.ShouldBe(2);
        roundTripped[0].PackageName.ShouldBe("pkg-a");
        roundTripped[1].PackageName.ShouldBe("pkg-b");
    }

    [Theory]
    [InlineData(Subject.Unit)]
    [InlineData(Subject.Agent)]
    public async Task SetAsync_EmptyList_ClearsTheRecord(Subject subject)
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, _, _) = Build(subject);

        await SetAsync(store, "subject-1", new[]
        {
            new SkillBundleReference("pkg-a", "skill-a"),
        }, ct);

        var cleared = await SetAsync(store, "subject-1", Array.Empty<SkillBundleReference>(), ct);

        cleared.ShouldBeEmpty();
        (await GetAsync(store, "subject-1", ct)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Subject.Unit)]
    [InlineData(Subject.Agent)]
    public async Task AddAsync_AppendsNewBundle(Subject subject)
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, _, _) = Build(subject);

        await AddAsync(store, "subject-1", new SkillBundleReference("pkg-a", "skill-a"), ct);
        var afterSecond = await AddAsync(store, "subject-1", new SkillBundleReference("pkg-b", "skill-b"), ct);

        afterSecond.Count.ShouldBe(2);
        afterSecond[0].SkillName.ShouldBe("skill-a");
        afterSecond[1].SkillName.ShouldBe("skill-b");
    }

    [Theory]
    [InlineData(Subject.Unit)]
    [InlineData(Subject.Agent)]
    public async Task AddAsync_IsIdempotentOnPackageAndSkill_RefreshesInPlace(Subject subject)
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, resolver, _) = Build(subject);

        // First add produces the baseline prompt.
        await AddAsync(store, "subject-1", new SkillBundleReference("pkg-a", "skill-a"), ct);
        await AddAsync(store, "subject-1", new SkillBundleReference("pkg-b", "skill-b"), ct);

        // Re-resolve the second reference with a different prompt to
        // prove the in-place refresh writes the freshest body.
        resolver.Override = r => new SkillBundle(
            PackageName: r.Package,
            SkillName: r.Skill,
            Prompt: "refreshed-prompt",
            RequiredTools: Array.Empty<SkillToolRequirement>());

        var afterReadd = await AddAsync(store, "subject-1", new SkillBundleReference("pkg-a", "skill-a"), ct);

        afterReadd.Count.ShouldBe(2); // No duplicate appended.
        afterReadd[0].SkillName.ShouldBe("skill-a");
        afterReadd[0].Prompt.ShouldBe("refreshed-prompt");
        // Ordering preserved — refreshed entry stays at index 0 (not
        // promoted to the tail) so manifest declaration-order semantics
        // hold.
        afterReadd[1].SkillName.ShouldBe("skill-b");
    }

    [Theory]
    [InlineData(Subject.Unit)]
    [InlineData(Subject.Agent)]
    public async Task RemoveAsync_DropsMatchingEntryAndPreservesOrder(Subject subject)
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, _, _) = Build(subject);

        await SetAsync(store, "subject-1", new[]
        {
            new SkillBundleReference("pkg-a", "skill-a"),
            new SkillBundleReference("pkg-b", "skill-b"),
            new SkillBundleReference("pkg-c", "skill-c"),
        }, ct);

        var afterRemove = await RemoveAsync(store, "subject-1", "pkg-b", "skill-b", ct);

        afterRemove.Count.ShouldBe(2);
        afterRemove[0].SkillName.ShouldBe("skill-a");
        afterRemove[1].SkillName.ShouldBe("skill-c");
    }

    [Theory]
    [InlineData(Subject.Unit)]
    [InlineData(Subject.Agent)]
    public async Task RemoveAsync_NoMatch_IsNoOp(Subject subject)
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, _, _) = Build(subject);

        await SetAsync(store, "subject-1", new[]
        {
            new SkillBundleReference("pkg-a", "skill-a"),
        }, ct);

        var unchanged = await RemoveAsync(store, "subject-1", "pkg-nope", "skill-nope", ct);

        unchanged.Count.ShouldBe(1);
        unchanged[0].SkillName.ShouldBe("skill-a");
    }

    [Theory]
    [InlineData(Subject.Unit)]
    [InlineData(Subject.Agent)]
    public async Task DeleteAsync_ClearsRecord(Subject subject)
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, _, _) = Build(subject);

        await SetAsync(store, "subject-1", new[]
        {
            new SkillBundleReference("pkg-a", "skill-a"),
        }, ct);

        await DeleteAsync(store, "subject-1", ct);

        (await GetAsync(store, "subject-1", ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task UnitAndAgentStores_UseDistinctKeyPrefixes()
    {
        var ct = TestContext.Current.CancellationToken;

        // Single shared backing store; the two implementations must not
        // collide on the same key for the same subject id.
        var state = new FakeStateStore();
        var resolver = new StubResolver();
        var unit = new StateStoreBackedUnitSkillBundleStore(state, resolver);
        var agent = new StateStoreBackedAgentSkillBundleStore(state, resolver);

        await unit.SetAsync("same-id", new[] { new SkillBundleReference("pkg-u", "u") }, ct);
        await agent.SetAsync("same-id", new[] { new SkillBundleReference("pkg-a", "a") }, ct);

        state.Items.Keys.ShouldContain("Unit:SkillBundles:same-id");
        state.Items.Keys.ShouldContain("Agent:SkillBundles:same-id");

        var unitBundles = await unit.GetAsync("same-id", ct);
        var agentBundles = await agent.GetAsync("same-id", ct);

        unitBundles.Single().PackageName.ShouldBe("pkg-u");
        agentBundles.Single().PackageName.ShouldBe("pkg-a");
    }

    [Theory]
    [InlineData(Subject.Unit)]
    [InlineData(Subject.Agent)]
    public async Task SetAsync_PersistsRequiredTools(Subject subject)
    {
        var ct = TestContext.Current.CancellationToken;
        var (store, _, resolver, _) = Build(subject);

        resolver.Override = r => new SkillBundle(
            PackageName: r.Package,
            SkillName: r.Skill,
            Prompt: "p",
            RequiredTools: new[]
            {
                new SkillToolRequirement("github.read", "read", EmptySchema, Optional: false),
                new SkillToolRequirement("github.write", "write", EmptySchema, Optional: true),
            });

        var written = await SetAsync(store, "subject-1", new[]
        {
            new SkillBundleReference("pkg-a", "skill-a"),
        }, ct);

        var roundTripped = await GetAsync(store, "subject-1", ct);
        roundTripped[0].RequiredTools.Count.ShouldBe(2);
        roundTripped[0].RequiredTools[0].Name.ShouldBe("github.read");
        roundTripped[0].RequiredTools[0].Optional.ShouldBeFalse();
        roundTripped[0].RequiredTools[1].Name.ShouldBe("github.write");
        roundTripped[0].RequiredTools[1].Optional.ShouldBeTrue();

        // Sanity: the resolver fired exactly once (no double-resolve
        // through the round-trip).
        resolver.CallCount.ShouldBe(1);
        _ = written;
    }
}
