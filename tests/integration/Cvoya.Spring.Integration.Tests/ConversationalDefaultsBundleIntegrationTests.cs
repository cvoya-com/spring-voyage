// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Prompts;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration coverage for ADR-0056 Wave 2 (#2657) — the
/// <c>sv.conversational.defaults</c> skill bundle. Exercises the bundle
/// end-to-end against the in-tree package on disk: resolve through
/// <see cref="FileSystemSkillBundleResolver"/>, equip via
/// <see cref="IAgentSkillBundleStore"/>, and assemble through
/// <see cref="PromptAssembler"/> to assert the
/// <c>[PLATFORM CONTRACT — NON-NEGOTIABLE]</c> header lands verbatim in
/// the agent's Layer 4 prompt section.
/// </summary>
/// <remarks>
/// This intentionally uses the same resolver + store + assembler stack
/// the production agent-create + dispatch path uses — pinning the
/// "fresh agent has the bundle out of the box" acceptance criterion
/// without standing up Dapr / EF / the HTTP host.
/// </remarks>
public class ConversationalDefaultsBundleIntegrationTests
{
    [Fact]
    public async Task EquippedOnAgent_AssembledPromptCarriesPlatformContractHeader()
    {
        var ct = TestContext.Current.CancellationToken;

        var resolver = BuildResolverFromRepoPackages();
        var state = new InMemoryStateStore();
        var agentStore = new StateStoreBackedAgentSkillBundleStore(state, resolver);

        var agentId = Guid.NewGuid().ToString("N");

        // The same call site the agent-create endpoint exercises
        // (`TryAddDefaultAgentSkillBundlesAsync` on a fresh agent).
        await agentStore.AddAsync(
            agentId,
            DefaultAgentSkillBundles.ConversationalDefaults,
            ct);

        var bundles = await agentStore.GetAsync(agentId, ct);
        bundles.Count.ShouldBe(1);

        var assembler = new PromptAssembler(
            BuildPlatformPromptProvider(),
            new UnitContextBuilder(),
            new ThreadContextBuilder(),
            new AgentInstructionsBuilder(),
            NullLoggerFactory.Instance);

        var context = new PromptAssemblyContext(
            Policies: null,
            Skills: null,
            PriorMessages: Array.Empty<Message>(),
            LastCheckpoint: null,
            AgentInstructions: "You are a helpful assistant.",
            AgentSkillBundles: bundles);

        var message = new Message(
            Guid.NewGuid(),
            Address.For("agent", agentId),
            Address.For("agent", agentId),
            MessageType.Domain,
            "thread-1",
            JsonSerializer.SerializeToElement(new { text = "Hello" }),
            DateTimeOffset.UtcNow);

        var assembled = await assembler.AssembleAsync(message, context, ct);

        // (1) The bracketed-upper-case marker — the load-bearing
        //     authority signal — is in the assembled prompt verbatim.
        assembled.ShouldContain("[PLATFORM CONTRACT — NON-NEGOTIABLE]");

        // (2) It lands inside Layer 4 (Agent Instructions), not before
        //     it. Layer 4 is where the agent-equipped bundles render,
        //     per EquippedBundleLoader's wiring.
        var layer4Idx = assembled.IndexOf("## Agent Instructions", StringComparison.Ordinal);
        var contractIdx = assembled.IndexOf("[PLATFORM CONTRACT — NON-NEGOTIABLE]", StringComparison.Ordinal);
        layer4Idx.ShouldBeGreaterThanOrEqualTo(0);
        contractIdx.ShouldBeGreaterThan(layer4Idx);

        // (3) The fundamental-core tool names appear inline so the
        //     runtime doesn't have to discover them before replying.
        assembled.ShouldContain("sv.messaging.send");
        assembled.ShouldContain("sv.directory.list");
        assembled.ShouldContain("sv.progress.report");
        assembled.ShouldContain("sv.tools.list_categories");

        // (4) The discovery-tool pointer is present so the runtime
        //     knows how to pull additional categories on demand.
        assembled.ShouldContain("sv.tools.list(");

        // (5) The memory category is named so the runtime can pull its
        //     tools on demand without first calling
        //     sv.tools.list_categories.
        assembled.ShouldContain("memory");
    }

    [Fact]
    public async Task EquippedOnAgent_BundleSurfacesFundamentalCoreToolsInRequiredList()
    {
        var ct = TestContext.Current.CancellationToken;

        var resolver = BuildResolverFromRepoPackages();
        var state = new InMemoryStateStore();
        var agentStore = new StateStoreBackedAgentSkillBundleStore(state, resolver);

        var agentId = Guid.NewGuid().ToString("N");
        await agentStore.AddAsync(
            agentId,
            DefaultAgentSkillBundles.ConversationalDefaults,
            ct);

        var bundles = await agentStore.GetAsync(agentId, ct);
        var bundle = bundles.Single();

        // The seven fundamental-core grants from ADR-0056 §8 must
        // survive the resolve → persist → re-resolve round-trip.
        // The bundle store re-resolves on each mutation; this asserts
        // the persisted record carries the right tool list rather than
        // dropping it on a JSON round-trip.
        var expected = new[]
        {
            "sv.messaging.send",
            "sv.messaging.multicast",
            "sv.directory.list",
            "sv.directory.lookup",
            "sv.progress.report",
            "sv.tools.list_categories",
            "sv.tools.list",
        };
        bundle.RequiredTools.Select(t => t.Name).ShouldBe(expected, ignoreOrder: true);
    }

    /// <summary>
    /// Points a <see cref="FileSystemSkillBundleResolver"/> at the
    /// in-tree <c>packages/</c> directory so the test reads the same
    /// on-disk bundle a production deployment ships.
    /// </summary>
    private static FileSystemSkillBundleResolver BuildResolverFromRepoPackages()
    {
        var repoRoot = ResolveRepoRoot();
        var packagesRoot = Path.Combine(repoRoot, "packages");
        Directory.Exists(packagesRoot).ShouldBeTrue(
            $"packages/ directory must exist under '{repoRoot}'.");

        return new FileSystemSkillBundleResolver(
            new SkillBundleOptions { PackagesRoot = packagesRoot },
            NullLogger<FileSystemSkillBundleResolver>.Instance);
    }

    private static IPlatformPromptProvider BuildPlatformPromptProvider()
    {
        var provider = Substitute.For<IPlatformPromptProvider>();
        provider.GetPlatformPromptAsync(Arg.Any<CancellationToken>())
            .Returns("Platform constraints go here.");
        return provider;
    }

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AGENTS.md")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not resolve repository root from AppContext.BaseDirectory.");
    }

    /// <summary>
    /// Minimal in-memory <see cref="IStateStore"/>. Round-trips through
    /// JSON serialisation so the persisted shape matches the production
    /// pattern (a SerialiserType-encoded JSON blob).
    /// </summary>
    private sealed class InMemoryStateStore : IStateStore
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> _items = new();

        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
        {
            if (_items.TryGetValue(key, out var bytes))
            {
                return Task.FromResult(JsonSerializer.Deserialize<T>(bytes));
            }
            return Task.FromResult<T?>(default);
        }

        public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
        {
            _items[key] = JsonSerializer.SerializeToUtf8Bytes(value);
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string key, CancellationToken ct = default)
        {
            _items.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public Task<bool> ContainsAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_items.ContainsKey(key));
    }
}
