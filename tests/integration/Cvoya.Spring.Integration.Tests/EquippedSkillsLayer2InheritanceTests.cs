// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Prompts;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Integration coverage for member-agent → parent-unit Layer 2 inheritance
/// (<see href="https://github.com/cvoya-com/spring-voyage/issues/2363">#2363</see>).
/// The sibling test <see cref="EquippedSkillsLayer4Tests"/> covers the
/// agent's own bundles landing in Layer 4; this fixture installs a
/// <c>kind: Skill</c> artefact, equips it on the <b>unit</b>, dispatches
/// a message to a member agent of that unit, and asserts the skill body
/// renders inside Layer 2 (Unit Context) — not Layer 4.
/// </summary>
public sealed class EquippedSkillsLayer2InheritanceTests : IDisposable
{
    private readonly string _rootDir;
    private readonly string _packageName;
    private readonly string _skillName;
    private readonly string _skillBody;

    public EquippedSkillsLayer2InheritanceTests()
    {
        var stamp = Guid.NewGuid().ToString("N")[..8];
        _rootDir = Path.Combine(
            Path.GetTempPath(),
            "spring-voyage-tests",
            $"equipped-skills-inherit-{stamp}");
        Directory.CreateDirectory(_rootDir);

        _packageName = $"layer2-{stamp}";
        _skillName = "team-policy";
        _skillBody = "## Team Policy\n\nAll work proceeds via the unit's standard sequence.";

        BuildSyntheticPackage();
    }

    public void Dispose()
    {
        try { Directory.Delete(_rootDir, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task EquipOnUnit_MemberAgentDispatch_BodyRendersInLayer2()
    {
        var ct = TestContext.Current.CancellationToken;

        var unitId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        // Stand up a minimal DI graph: in-memory EF for membership +
        // unit-display-name lookups, in-memory state-store backing for
        // both bundle stores, a file-system bundle resolver pointed at
        // the temp package root.
        var dbName = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(OssTenantIds.Default));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddScoped<IUnitMembershipRepository, UnitMembershipRepository>();

        var state = new InMemoryStateStore();
        var resolverOptions = Microsoft.Extensions.Options.Options.Create(
            new SkillBundleOptions { PackagesRoot = _rootDir });
        ISkillBundleResolver resolver = new FileSystemSkillBundleResolver(
            resolverOptions,
            NullLogger<FileSystemSkillBundleResolver>.Instance);
        var unitStore = new StateStoreBackedUnitSkillBundleStore(state, resolver);
        var agentStore = new StateStoreBackedAgentSkillBundleStore(state, resolver);
        services.AddSingleton<IUnitSkillBundleStore>(unitStore);
        services.AddSingleton<IAgentSkillBundleStore>(agentStore);

        await using var sp = services.BuildServiceProvider();

        // Seed the unit-definition row (display-name lookup target) and
        // the membership row that ties the agent to the unit.
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            await db.Database.EnsureCreatedAsync(ct);
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = unitId,
                DisplayName = "Engineering Team",
            });
            db.UnitMemberships.Add(new UnitMembershipEntity
            {
                UnitId = unitId,
                AgentId = agentId,
                Enabled = true,
                IsPrimary = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        // Equip the skill on the UNIT — exactly the same write the
        // POST /api/v1/tenant/units/{id}/skills endpoint performs.
        await unitStore.AddAsync(
            GuidFormatter.Format(unitId),
            new SkillBundleReference(_packageName, _skillName),
            ct);

        // Dispatch a message to the MEMBER AGENT. EquippedBundleLoader
        // is the production seam AgentActor.LoadEquippedBundlesAsync
        // delegates to, so this exercises the same code path that runs
        // inside the actor at message-turn time.
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var (unitBundles, agentBundles) = await EquippedBundleLoader.LoadAsync(
            scopeFactory,
            GuidFormatter.Format(agentId),
            ct);

        unitBundles.ShouldNotBeNull();
        unitBundles.Count.ShouldBe(1);
        unitBundles[0].PackageName.ShouldBe(_packageName);
        unitBundles[0].SkillName.ShouldBe(_skillName);
        unitBundles[0].Prompt.ShouldContain("Team Policy");
        agentBundles.ShouldBeNull();

        // Compose the prompt with the loader's output. The skill body
        // must land in Layer 2 (Unit Context), NOT Layer 4 (Agent
        // Instructions) — that's the acceptance criterion from #2363.
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
            AgentInstructions: "You are the member agent.",
            SkillBundles: unitBundles,
            AgentSkillBundles: agentBundles);

        var assembled = await assembler.AssembleAsync(context, ct);

        // Acceptance: body present, AND it lands after the Unit Context
        // header but before the Agent Instructions header.
        assembled.ShouldContain("Team Policy");
        assembled.ShouldContain("You are the member agent.");

        var layer2Idx = assembled.IndexOf("## Unit Context", StringComparison.Ordinal);
        var bodyIdx = assembled.IndexOf("Team Policy", StringComparison.Ordinal);
        var layer4Idx = assembled.IndexOf("## Agent Instructions", StringComparison.Ordinal);

        layer2Idx.ShouldBeGreaterThanOrEqualTo(0,
            "unit context section must be present when bundles are inherited");
        bodyIdx.ShouldBeGreaterThan(layer2Idx,
            "inherited skill body must render inside Layer 2 (after the Unit Context header)");
        if (layer4Idx >= 0)
        {
            bodyIdx.ShouldBeLessThan(layer4Idx,
                "inherited skill body must NOT render inside Layer 4 (Agent Instructions)");
        }
    }

    private void BuildSyntheticPackage()
    {
        var pkgRoot = Path.Combine(_rootDir, _packageName);
        Directory.CreateDirectory(pkgRoot);
        File.WriteAllText(Path.Combine(pkgRoot, "package.yaml"), $"""
            apiVersion: spring.voyage/v1
            kind: Package
            name: {_packageName}
            description: Synthetic single-skill package for the #2363 Layer 2 inheritance test.
            version: 1.0.0
            """);

        var skillDir = Path.Combine(pkgRoot, "skills", _skillName);
        Directory.CreateDirectory(skillDir);
        File.WriteAllText(Path.Combine(skillDir, "package.yaml"), $"""
            apiVersion: spring.voyage/v1
            kind: Skill
            name: {_skillName}
            description: Synthetic team policy skill asserted by the #2363 inheritance test.
            """);
        File.WriteAllText(Path.Combine(skillDir, $"{_skillName}.md"), _skillBody);
    }

    /// <summary>
    /// Minimal in-memory <see cref="IStateStore"/>, mirroring the helper
    /// the sibling <see cref="EquippedSkillsLayer4Tests"/> uses. Round-
    /// trips through JSON so the persisted shape matches production.
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
