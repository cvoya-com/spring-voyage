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
/// Integration coverage for the Layer 4 agent-skill injection wired in
/// #2360. The test plants a synthetic package on temp disk with a
/// <c>kind: Skill</c> artefact, equips the skill on an agent through
/// the same <see cref="IAgentSkillBundleStore"/> the operator-equip
/// endpoint exercises, then composes a prompt through
/// <see cref="PromptAssembler"/>. The assertion is the acceptance
/// criterion from #2360: the assembled prompt carries the skill's body
/// in the Layer 4 (agent instructions) section.
/// </summary>
public class EquippedSkillsLayer4Tests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _packageName;
    private readonly string _skillName;
    private readonly string _skillBody;

    public EquippedSkillsLayer4Tests()
    {
        var stamp = Guid.NewGuid().ToString("N")[..8];
        _rootDir = Path.Combine(
            Path.GetTempPath(),
            "spring-voyage-tests",
            $"equipped-skills-{stamp}");
        Directory.CreateDirectory(_rootDir);

        _packageName = $"layer4-{stamp}";
        _skillName = "synthetic-skill";
        _skillBody = "## Synthetic skill prompt\n\nWhen invoked, do the synthetic thing.";

        BuildSyntheticPackage();
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task EquipOnAgent_AssembledPromptContainsBodyInLayer4()
    {
        var ct = TestContext.Current.CancellationToken;

        // 1) Resolver pointed at the temp package root. Bypass the
        //    tenant-filtering decorator because the test doesn't stand
        //    up the tenant-binding pipeline.
        var resolverOptions = Microsoft.Extensions.Options.Options.Create(
            new SkillBundleOptions { PackagesRoot = _rootDir });
        ISkillBundleResolver resolver = new FileSystemSkillBundleResolver(
            resolverOptions,
            NullLogger<FileSystemSkillBundleResolver>.Instance);

        // 2) In-memory state-store. The agent store namespaces under
        //    `Agent:SkillBundles:` so it doesn't collide with anything.
        var state = new InMemoryStateStore();
        var agentStore = new StateStoreBackedAgentSkillBundleStore(state, resolver);

        var agentId = Guid.NewGuid().ToString("N");

        // 3) Equip the skill — the same path the POST endpoint hits.
        await agentStore.AddAsync(
            agentId,
            new SkillBundleReference(_packageName, _skillName),
            ct);

        // 4) Read back and feed into the prompt assembler.
        var bundles = await agentStore.GetAsync(agentId, ct);
        bundles.Count.ShouldBe(1);
        bundles[0].PackageName.ShouldBe(_packageName);
        bundles[0].SkillName.ShouldBe(_skillName);
        bundles[0].Prompt.ShouldContain("Synthetic skill prompt");

        var platformProvider = Substitute.For<IPlatformPromptProvider>();
        platformProvider.GetPlatformPromptAsync(Arg.Any<CancellationToken>())
            .Returns("Platform constraints go here.");
        var assembler = new PromptAssembler(
            platformProvider,
            new UnitContextBuilder(),
            new AgentInstructionsBuilder(),
            NullLoggerFactory.Instance);

        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "You are the synthetic agent.",
            AgentSkillBundles: bundles);

        var assembled = await assembler.AssembleAsync(context, ct);

        // 5) The acceptance criterion: the assembled prompt carries the
        //    skill body in Layer 4. Two sub-assertions: (a) the body is
        //    present, and (b) it lands after the Layer 4 header — not in
        //    Layer 2.
        assembled.ShouldContain("Synthetic skill prompt");
        assembled.ShouldContain("You are the synthetic agent.");

        var layer4Idx = assembled.IndexOf("## Agent Instructions", StringComparison.Ordinal);
        var bodyIdx = assembled.IndexOf("Synthetic skill prompt", StringComparison.Ordinal);
        layer4Idx.ShouldBeGreaterThanOrEqualTo(0,
            "agent instructions section must be present when bundles are equipped");
        bodyIdx.ShouldBeGreaterThan(layer4Idx,
            "agent-equipped skill body must render inside Layer 4, not before it");

        // And it must NOT appear under Layer 2 — agent bundles are
        // strictly a Layer 4 concern.
        assembled.ShouldNotContain("## Unit Context");
    }

    private void BuildSyntheticPackage()
    {
        // ADR-0043 recursive layout — packages/<pkg>/skills/<skill>/<skill>.md
        // with a co-located package.yaml so the file-system resolver
        // accepts it.
        var pkgRoot = Path.Combine(_rootDir, _packageName);
        Directory.CreateDirectory(pkgRoot);
        File.WriteAllText(Path.Combine(pkgRoot, "package.yaml"), $"""
            apiVersion: spring.voyage/v1
            kind: Package
            name: {_packageName}
            description: Synthetic single-skill package for the #2360 integration test.
            version: 1.0.0
            """);

        var skillDir = Path.Combine(pkgRoot, "skills", _skillName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "package.yaml"), $"""
            apiVersion: spring.voyage/v1
            kind: Skill
            name: {_skillName}
            description: Synthetic skill body asserted by the Layer 4 integration test.
            """);
        File.WriteAllText(Path.Combine(skillDir, $"{_skillName}.md"), _skillBody);
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
