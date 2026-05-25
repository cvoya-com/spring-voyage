// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Prompts;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration coverage for the
/// <c>sv.conversational.defaults</c> skill bundle. Exercises the bundle
/// end-to-end against the in-tree package on disk: resolve through
/// <see cref="FileSystemSkillBundleResolver"/>, equip via
/// <see cref="IAgentSkillBundleStore"/>, and assemble through
/// <see cref="PromptAssembler"/> to assert the
/// <c>### Platform Contract — Non-Negotiable</c> heading (level per
/// #2738 — promoted from the legacy bracketed marker) lands in the
/// agent's Layer 1 (Platform Instructions) section — and is NOT
/// duplicated in the conversational-defaults bundle text rendered in
/// Layer 4.
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
    public async Task EquippedOnAgent_AssembledPromptCarriesPlatformContractHeaderInLayer1()
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

        // Use the real PlatformPromptProvider so Layer 1 carries the
        // production `### Platform Contract — Non-Negotiable` block
        // verbatim (heading level per #2738).
        var assembler = new PromptAssembler(
            new PlatformPromptProvider(),
            new UnitContextBuilder(),
            new AgentInstructionsBuilder(),
            NullLoggerFactory.Instance);

        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "You are a helpful assistant.",
            AgentSkillBundles: bundles);

        var assembled = await assembler.AssembleAsync(context, ct);

        // (1) The contract heading — the load-bearing authority signal,
        //     promoted from the legacy bracketed marker per #2738 — is
        //     in the assembled prompt verbatim.
        assembled.ShouldContain("### Platform Contract — Non-Negotiable");
        assembled.ShouldNotContain("[PLATFORM CONTRACT — NON-NEGOTIABLE]");
        assembled.ShouldNotContain("[END PLATFORM CONTRACT]");

        // (2) The contract lands inside Layer 1 (Platform Instructions)
        //     — not inside Layer 4. After the Wave 3 cutover the bundle
        //     text no longer carries the contract; it is provided once
        //     by the platform-prompt provider at the top of the prompt.
        var layer1Idx = assembled.IndexOf("## Platform Instructions", StringComparison.Ordinal);
        var layer4Idx = assembled.IndexOf("## Role-specific instructions", StringComparison.Ordinal);
        var contractIdx = assembled.IndexOf("### Platform Contract — Non-Negotiable", StringComparison.Ordinal);
        layer1Idx.ShouldBeGreaterThanOrEqualTo(0);
        layer4Idx.ShouldBeGreaterThan(layer1Idx);
        contractIdx.ShouldBeGreaterThan(layer1Idx);
        contractIdx.ShouldBeLessThan(layer4Idx);

        // (3) The bundle text (in Layer 4) starts by pointing back to
        //     the platform-layer contract rather than duplicating it.
        assembled.ShouldContain("platform's response contract");
        assembled.ShouldContain("platform-layer instructions");

        // (4) There must be only one occurrence of the contract header
        //     — the bundle no longer carries a parallel copy.
        var firstHeader = assembled.IndexOf("### Platform Contract — Non-Negotiable", StringComparison.Ordinal);
        var secondHeader = assembled.IndexOf("### Platform Contract — Non-Negotiable", firstHeader + 1, StringComparison.Ordinal);
        secondHeader.ShouldBe(-1);

        // (5) The fundamental-core tool names appear inline — they
        //     are rendered by Layer 1 (PlatformPromptProvider) since
        //     #2670, not by the bundle. The runtime sees them at the
        //     top of the prompt without any equipped bundle being
        //     required.
        assembled.ShouldContain("sv.messaging.send");
        assembled.ShouldContain("sv.directory.list");
        assembled.ShouldContain("sv.progress.report");
        assembled.ShouldContain("sv.tools.list_categories");

        // (6) The discovery-tool pointer is present so the runtime
        //     knows how to pull additional categories on demand.
        assembled.ShouldContain("sv.tools.list(");

        // (7) The memory category is named — the bundle's only
        //     bundle-specific contribution after #2670. Surfaced so
        //     the runtime knows the category exists without first
        //     calling sv.tools.list_categories.
        assembled.ShouldContain("memory");

        // (8) #2670 / #2681: the platform-instructions section owns the
        //     fundamental-core tool names — they may appear multiple
        //     times inside that section (the catalog plus the #2681
        //     non-example refer to sv.messaging.send), but no downstream
        //     section may re-name them. Check by asserting no occurrence
        //     after the role-specific instructions heading.
        var roleIdx = assembled.IndexOf("## Role-specific instructions", StringComparison.Ordinal);
        roleIdx.ShouldBeGreaterThan(0);
        var sendInRoleSection = assembled.IndexOf("sv.messaging.send", roleIdx, StringComparison.Ordinal);
        sendInRoleSection.ShouldBe(-1, "The platform-instructions section owns the platform-tool catalog; no downstream section may re-name sv.messaging.send.");
    }

    [Fact]
    public async Task EquippedOnAgent_BundleEmitsNoRequiredToolsAfterCoreCatalogMovedToLayer1()
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

        // #2670: the bundle no longer re-grants the fundamental-core
        // tools through RequiredTools. ToolGrantResolver.EnumeratePlatformTools
        // grants every sv.* registry tool with provenance=Platform on
        // its own; surfacing the same names through the bundle would
        // double up the rendered Required tools sub-section and
        // duplicate the Layer 1 catalog.
        bundle.RequiredTools.ShouldBeEmpty(
            "After #2670 the bundle is prompt-only; the platform-tool catalog lives in Layer 1.");
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
                return Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<T>(bytes));
            }
            return Task.FromResult<T?>(default);
        }

        public Task SetAsync<T>(string key, T value, CancellationToken ct = default)
        {
            _items[key] = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
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
