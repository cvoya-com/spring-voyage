// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="ToolGrantResolver"/> (#2335 Sub B). Covers the
/// four provenance tiers — implicit <c>sv.*</c>, connector grants
/// (direct and inherited), defensive image-tier reads, and explicit
/// rows — and the precedence resolution between them.
/// </summary>
public class ToolGrantResolverTests : IDisposable
{
    private static readonly Guid GitHubTypeId = new("11111111-1111-1111-1111-111111111111");

    private static readonly Guid Unit1 = new("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid Unit2 = new("dddddddd-0000-0000-0000-000000000002");
    private static readonly Guid Agent1 = new("eeeeeeee-0000-0000-0000-000000000001");

    private readonly ServiceProvider _serviceProvider;
    private readonly string _dbName = Guid.NewGuid().ToString();

    public ToolGrantResolverTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(OssTenantIds.Default));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(_dbName));

        // The resolver opens a scope and resolves these per call.
        services.AddScoped<IUnitConnectorBindingRepository, UnitConnectorBindingRepository>();
        services.AddScoped<IUnitMembershipRepository, UnitMembershipRepository>();
        services.AddScoped<IUnitSubunitMembershipRepository, UnitSubunitMembershipRepository>();
        services.AddSingleton<IImageToolsReader, EmptyImageToolsReader>();

        _serviceProvider = services.BuildServiceProvider();
    }

    private ToolGrantResolver CreateResolver(
        IEnumerable<ISkillRegistry>? registries = null,
        IEnumerable<IConnectorType>? connectorTypes = null)
    {
        return new ToolGrantResolver(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            registries ?? new[] { (ISkillRegistry)new FakeSkillRegistry("sv", new[] { "sv.directory.get_self", "sv.directory.list_members" }) },
            connectorTypes ?? Array.Empty<IConnectorType>(),
            NullLogger<ToolGrantResolver>.Instance);
    }

    private async Task<SpringDbContext> CreateContextAsync()
    {
        var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    [Fact]
    public async Task ResolveAsync_AgentWithNothing_ReturnsOnlyPlatformTools()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = CreateResolver();

        var effective = await resolver.ResolveAsync(
            new Address(Address.AgentScheme, Agent1), ct);

        effective.Count.ShouldBe(2);
        effective.ShouldAllBe(t => t.Provenance == ToolProvenance.Platform);
        effective.Select(t => t.Name).ShouldBe(new[] { "sv.directory.get_self", "sv.directory.list_members" });
    }

    [Fact]
    public async Task ResolveAsync_UnitWithNothing_ReturnsOnlyPlatformTools()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = CreateResolver();

        var effective = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), ct);

        effective.Count.ShouldBe(2);
        effective.ShouldAllBe(t => t.Provenance == ToolProvenance.Platform);
    }

    [Fact]
    public async Task ResolveAsync_UnitBoundToConnector_SurfacesNamespaceTools()
    {
        var ct = TestContext.Current.CancellationToken;

        // Arrange — bind the unit to the GitHub connector type.
        await using (var db = await CreateContextAsync())
        {
            db.UnitConnectorBindings.Add(new UnitConnectorBindingEntity
            {
                Id = Guid.NewGuid(),
                UnitId = Unit1,
                ConnectorType = GitHubTypeId,
                Config = JsonSerializer.SerializeToElement(new { }),
                BoundAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var resolver = CreateResolver(
            registries: new ISkillRegistry[]
            {
                new FakeSkillRegistry("sv", new[] { "sv.directory.get_self" }),
                new FakeSkillRegistry("github", new[] { "github.create_issue", "github.close_issue" }),
            },
            connectorTypes: new[] { (IConnectorType)new FakeConnectorType(GitHubTypeId, "github", "github") });

        var effective = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), ct);

        var ghTools = effective.Where(t => t.Namespace == "github").ToList();
        ghTools.Count.ShouldBe(2);
        ghTools.ShouldAllBe(t => t.Provenance == "connector:github");
        ghTools.ShouldAllBe(t => t.InheritedFromUnitName == null);
    }

    [Fact]
    public async Task ResolveAsync_AgentInheritsConnectorFromParentUnit()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var db = await CreateContextAsync())
        {
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = Unit1,
                DisplayName = "Parent Unit",
            });
            db.UnitConnectorBindings.Add(new UnitConnectorBindingEntity
            {
                Id = Guid.NewGuid(),
                UnitId = Unit1,
                ConnectorType = GitHubTypeId,
                Config = JsonSerializer.SerializeToElement(new { }),
                BoundAt = DateTimeOffset.UtcNow,
            });
            db.UnitMemberships.Add(new UnitMembershipEntity
            {
                UnitId = Unit1,
                AgentId = Agent1,
                Enabled = true,
                IsPrimary = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var resolver = CreateResolver(
            registries: new ISkillRegistry[]
            {
                new FakeSkillRegistry("github", new[] { "github.create_issue" }),
            },
            connectorTypes: new[] { (IConnectorType)new FakeConnectorType(GitHubTypeId, "github", "github") });

        var effective = await resolver.ResolveAsync(
            new Address(Address.AgentScheme, Agent1), ct);

        var ghTool = effective.SingleOrDefault(t => t.Name == "github.create_issue");
        ghTool.ShouldNotBeNull();
        ghTool.Provenance.ShouldBe("connector:github");
        ghTool.InheritedFromUnitName.ShouldBe("Parent Unit");
    }

    [Fact]
    public async Task ResolveAsync_ExplicitRow_TakesPrecedenceOverConnector()
    {
        var ct = TestContext.Current.CancellationToken;

        await using (var db = await CreateContextAsync())
        {
            db.UnitConnectorBindings.Add(new UnitConnectorBindingEntity
            {
                Id = Guid.NewGuid(),
                UnitId = Unit1,
                ConnectorType = GitHubTypeId,
                Config = JsonSerializer.SerializeToElement(new { }),
                BoundAt = DateTimeOffset.UtcNow,
            });
            db.UnitToolGrants.Add(new UnitToolGrantEntity
            {
                UnitId = Unit1,
                Namespace = "github",
                ToolName = "github.create_issue",
                Provenance = ToolProvenance.Explicit,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var resolver = CreateResolver(
            registries: new ISkillRegistry[]
            {
                new FakeSkillRegistry("github", new[] { "github.create_issue" }),
            },
            connectorTypes: new[] { (IConnectorType)new FakeConnectorType(GitHubTypeId, "github", "github") });

        var effective = await resolver.ResolveAsync(
            new Address(Address.UnitScheme, Unit1), ct);

        var ghTool = effective.Single(t => t.Name == "github.create_issue");
        ghTool.Provenance.ShouldBe(ToolProvenance.Explicit);
    }

    [Fact]
    public async Task ResolveAsync_PlatformTier_DoesNotRequireRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = CreateResolver();

        // No row at all in the DB.
        var effective = await resolver.ResolveAsync(
            new Address(Address.AgentScheme, Agent1), ct);

        effective.ShouldContain(t => t.Name == "sv.directory.get_self" && t.Provenance == ToolProvenance.Platform);
        effective.ShouldContain(t => t.Name == "sv.directory.list_members" && t.Provenance == ToolProvenance.Platform);
    }

    [Fact]
    public async Task ResolveAsync_DefensiveImageReader_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        // The empty image-tools reader is the default — confirm it
        // contributes no tools and never throws.
        var resolver = CreateResolver();

        var effective = await resolver.ResolveAsync(
            new Address(Address.AgentScheme, Agent1), ct);

        effective.ShouldNotContain(t => t.Provenance.StartsWith("image:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveAsync_ThrowingImageReader_IsSwallowedAsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;

        // Build a fresh container with a misbehaving image reader, simulating
        // the pre-Sub-C state where the storage column may not be present.
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(OssTenantIds.Default));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(_dbName));
        services.AddScoped<IUnitConnectorBindingRepository, UnitConnectorBindingRepository>();
        services.AddScoped<IUnitMembershipRepository, UnitMembershipRepository>();
        services.AddScoped<IUnitSubunitMembershipRepository, UnitSubunitMembershipRepository>();
        services.AddSingleton<IImageToolsReader, ThrowingImageToolsReader>();

        await using var sp = services.BuildServiceProvider();

        var resolver = new ToolGrantResolver(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ISkillRegistry[] { new FakeSkillRegistry("sv", new[] { "sv.directory.get_self" }) },
            Array.Empty<IConnectorType>(),
            NullLogger<ToolGrantResolver>.Instance);

        var effective = await resolver.ResolveAsync(
            new Address(Address.AgentScheme, Agent1), ct);

        // Image tier yields nothing — platform tools still surface.
        effective.ShouldContain(t => t.Provenance == ToolProvenance.Platform);
        effective.ShouldNotContain(t => t.Provenance.StartsWith("image:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ResolveAsync_NonSubjectScheme_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var resolver = CreateResolver();

        await Should.ThrowAsync<Cvoya.Spring.Core.SpringException>(() =>
            resolver.ResolveAsync(new Address("human", Guid.NewGuid()), ct));
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class FakeSkillRegistry(string name, IReadOnlyList<string> toolNames) : ISkillRegistry
    {
        public string Name => name;

        public IReadOnlyList<ToolDefinition> GetToolDefinitions()
        {
            var schema = JsonDocument.Parse("""{"type":"object"}""").RootElement.Clone();
            return toolNames
                .Select(n => new ToolDefinition(n, $"desc({n})", schema, string.Empty))
                .ToList();
        }

        public Task<JsonElement> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeConnectorType(Guid typeId, string slug, string toolNamespace) : IConnectorType
    {
        public Guid TypeId => typeId;
        public string Slug => slug;
        public string ToolNamespace => toolNamespace;
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

    private sealed class ThrowingImageToolsReader : IImageToolsReader
    {
        public Task<IReadOnlyList<ImageToolEntry>> GetImageToolsAsync(
            Address subject, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("simulated missing column");
    }
}
