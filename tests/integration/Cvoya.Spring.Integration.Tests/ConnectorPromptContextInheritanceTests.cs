// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Prompts;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration coverage for the connector prompt-context seam (#2442)
/// against the production binding store and unit-hierarchy resolver.
/// Wires a real <see cref="ConnectorPromptContextResolver"/> +
/// <see cref="PromptAssembler"/> over an in-memory EF DbContext, then
/// verifies that a launch on a unit with a bound connector renders the
/// platform-injected "Connector context" section under Layer 1.
/// </summary>
public class ConnectorPromptContextInheritanceTests : IDisposable
{
    private static readonly Guid ConnectorTypeId = new("44444444-aaaa-bbbb-cccc-000000000001");
    private static readonly Guid LeafUnit = new("55555555-aaaa-bbbb-cccc-000000000001");
    private static readonly Guid ParentUnit = new("55555555-aaaa-bbbb-cccc-000000000002");
    private static readonly Guid TenantId = OssTenantIds.Default;

    private readonly ServiceProvider _services;
    private readonly string _dbName = Guid.NewGuid().ToString();

    public ConnectorPromptContextInheritanceTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantId));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(_dbName));
        services.AddScoped<IUnitConnectorBindingRepository, UnitConnectorBindingRepository>();
        services.AddScoped<IUnitSubunitMembershipRepository, UnitSubunitMembershipRepository>();
        services.AddScoped<IUnitMembershipRepository, UnitMembershipRepository>();
        services.AddSingleton<IUnitConnectorBindingStore, UnitConnectorBindingStore>();
        services.AddSingleton<IUnitHierarchyResolver, Cvoya.Spring.Dapr.Auth.DirectoryUnitHierarchyResolver>();
        services.AddLogging();
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<SpringDbContext> CreateDbAsync()
    {
        var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    [Fact]
    public async Task FullLaunch_ConnectorBoundOnParent_PromptIncludesConnectorContextSection()
    {
        var ct = TestContext.Current.CancellationToken;

        // Bind the parent unit to a connector, and link the leaf as a
        // subunit. The leaf's launch should inherit the binding.
        await using (var db = await CreateDbAsync())
        {
            db.UnitSubunitMemberships.Add(new UnitSubunitMembershipEntity
            {
                ParentId = ParentUnit,
                ChildId = LeafUnit,
            });
            await db.SaveChangesAsync(ct);
        }

        var bindingStore = _services.GetRequiredService<IUnitConnectorBindingStore>();
        await bindingStore.SetAsync(
            ParentUnit, ConnectorTypeId,
            JsonSerializer.SerializeToElement(new { owner = "cvoya-com", repo = "spring-voyage" }),
            ct);

        // Wire the resolver + assembler over the production binding store.
        var contributor = new StubPromptContributor(
            ConnectorTypeId,
            "#### GitHub binding — cvoya-com/spring-voyage\nYour container has env-vars set.");
        var walker = new ConnectorBindingWalker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            bindingStore,
            _services.GetRequiredService<IUnitHierarchyResolver>(),
            NullLogger<ConnectorBindingWalker>.Instance);
        var resolver = new ConnectorPromptContextResolver(
            walker,
            [new FakeConnectorType(ConnectorTypeId, "test-connector")],
            [contributor],
            NullLogger<ConnectorPromptContextResolver>.Instance);
        var assembler = new PromptAssembler(
            new PlatformPromptProvider(),
            new UnitContextBuilder(),
            new AgentInstructionsBuilder(),
            NullLoggerFactory.Instance);

        var fragments = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, LeafUnit), ct);
        var context = new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: "agent body",
            ConnectorPromptFragments: fragments);

        var prompt = await assembler.AssembleAsync(context, ct);

        prompt.ShouldContain("## Platform Instructions");
        prompt.ShouldContain("### Connector context (auto-injected by platform)");
        prompt.ShouldContain("#### GitHub binding — cvoya-com/spring-voyage");
        prompt.ShouldContain("Your container has env-vars set.");
        prompt.ShouldContain("## Role-specific instructions");
    }

    private sealed class StubPromptContributor(Guid connectorTypeId, string fragment)
        : IConnectorPromptContextContributor
    {
        public Guid ConnectorTypeId { get; } = connectorTypeId;

        public Task<string?> GetPromptHintsAsync(
            Address subject,
            Guid bindingOwnerUnitId,
            UnitConnectorBinding binding,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(fragment);
    }

    private sealed class FakeConnectorType(Guid typeId, string slug) : IConnectorType
    {
        public Guid TypeId => typeId;
        public string Slug => slug;
        public string DisplayName => slug;
        public string Description => $"{slug} connector";
        public Type ConfigType => typeof(object);

        public void MapRoutes(IEndpointRouteBuilder group) { }

        public Task<JsonElement?> GetConfigSchemaAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<JsonElement?>(null);

        public Task OnUnitStartingAsync(string unitId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task OnUnitStoppingAsync(string unitId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
