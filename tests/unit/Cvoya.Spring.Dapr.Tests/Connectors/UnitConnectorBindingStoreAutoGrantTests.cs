// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Connectors;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Tests the auto-grant pipeline on <see cref="UnitConnectorBindingStore"/>
/// (#2335 Sub B). Asserts that binding a connector writes one
/// <c>unit_tool_grants</c> row per <c>&lt;ToolNamespace&gt;.*</c> tool
/// with <c>provenance = "connector:&lt;Slug&gt;"</c>, unbinding revokes
/// them, and re-binds swap rows atomically.
/// </summary>
public class UnitConnectorBindingStoreAutoGrantTests : IDisposable
{
    private static readonly Guid GitHubTypeId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SlackTypeId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Unit1 = new("dddddddd-0000-0000-0000-000000000001");

    private ServiceProvider _serviceProvider;
    private readonly string _dbName = Guid.NewGuid().ToString();

    public UnitConnectorBindingStoreAutoGrantTests()
    {
        _serviceProvider = BuildServiceProvider(Array.Empty<IConnectorType>());
    }

    private ServiceProvider BuildServiceProvider(IConnectorType[] connectorTypes)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(OssTenantIds.Default));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(_dbName));
        services.AddScoped<IUnitConnectorBindingRepository, UnitConnectorBindingRepository>();
        foreach (var ct in connectorTypes)
        {
            services.AddSingleton<IConnectorType>(ct);
        }
        services.AddSingleton<ISkillRegistry>(
            new FakeSkillRegistry("github", new[] { "github.create_issue", "github.close_issue" }));
        services.AddSingleton<ISkillRegistry>(
            new FakeSkillRegistry("slack", new[] { "slack.send_message" }));
        return services.BuildServiceProvider();
    }

    private UnitConnectorBindingStore CreateStore(
        params IConnectorType[] connectorTypes)
    {
        // Rebuild the provider so test-supplied connector types are available
        // via DI — UnitConnectorBindingStore now resolves IConnectorType from
        // the per-call scope rather than the constructor (breaks the
        // UnitActorConnectorConfigStore → binding-store singleton cycle).
        _serviceProvider.Dispose();
        _serviceProvider = BuildServiceProvider(connectorTypes);
        return new UnitConnectorBindingStore(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<UnitConnectorBindingStore>.Instance);
    }

    private async Task<SpringDbContext> CreateContextAsync()
    {
        var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        await db.Database.EnsureCreatedAsync();
        return db;
    }

    [Fact]
    public async Task SetAsync_WritesOneRowPerNamespaceTool()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateContextAsync();

        var store = CreateStore(new FakeConnectorType(GitHubTypeId, "github", "github"));
        await store.SetAsync(Unit1, GitHubTypeId, JsonSerializer.SerializeToElement(new { }), ct);

        await using var db = await CreateContextAsync();
        var rows = await db.UnitToolGrants.Where(g => g.UnitId == Unit1).ToListAsync(ct);
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.Provenance == "connector:github");
        rows.Select(r => r.ToolName).ShouldBe(new[] { "github.create_issue", "github.close_issue" }, ignoreOrder: true);
    }

    [Fact]
    public async Task ClearAsync_RemovesAutoGrantedRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateContextAsync();

        var store = CreateStore(new FakeConnectorType(GitHubTypeId, "github", "github"));
        await store.SetAsync(Unit1, GitHubTypeId, JsonSerializer.SerializeToElement(new { }), ct);
        await store.ClearAsync(Unit1, ct);

        await using var db = await CreateContextAsync();
        var rows = await db.UnitToolGrants.Where(g => g.UnitId == Unit1).ToListAsync(ct);
        rows.ShouldBeEmpty();
    }

    [Fact]
    public async Task SetAsync_Rebind_SwapsNamespaceRowsAtomically()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateContextAsync();

        var store = CreateStore(
            new FakeConnectorType(GitHubTypeId, "github", "github"),
            new FakeConnectorType(SlackTypeId, "slack", "slack"));
        await store.SetAsync(Unit1, GitHubTypeId, JsonSerializer.SerializeToElement(new { }), ct);
        await store.SetAsync(Unit1, SlackTypeId, JsonSerializer.SerializeToElement(new { }), ct);

        await using var db = await CreateContextAsync();
        var rows = await db.UnitToolGrants.Where(g => g.UnitId == Unit1).ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].Provenance.ShouldBe("connector:slack");
        rows[0].ToolName.ShouldBe("slack.send_message");
    }

    [Fact]
    public async Task SetAsync_Idempotent_DoesNotDuplicateRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateContextAsync();

        var store = CreateStore(new FakeConnectorType(GitHubTypeId, "github", "github"));
        await store.SetAsync(Unit1, GitHubTypeId, JsonSerializer.SerializeToElement(new { }), ct);
        await store.SetAsync(Unit1, GitHubTypeId, JsonSerializer.SerializeToElement(new { }), ct);

        await using var db = await CreateContextAsync();
        var rows = await db.UnitToolGrants.Where(g => g.UnitId == Unit1).ToListAsync(ct);
        rows.Count.ShouldBe(2);
    }

    [Fact]
    public async Task SetAsync_UnknownConnectorType_DoesNotThrow_DoesNotGrant()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateContextAsync();

        // Store registered with no connector types — binding to an unknown
        // type id must still bind, just with no namespace auto-grant.
        var store = CreateStore();
        await store.SetAsync(Unit1, GitHubTypeId, JsonSerializer.SerializeToElement(new { }), ct);

        await using var db = await CreateContextAsync();
        var binding = await db.UnitConnectorBindings.SingleAsync(b => b.UnitId == Unit1, ct);
        binding.ConnectorType.ShouldBe(GitHubTypeId);
        var grants = await db.UnitToolGrants.Where(g => g.UnitId == Unit1).ToListAsync(ct);
        grants.ShouldBeEmpty();
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
                .Select(n => new ToolDefinition(n, $"desc({n})", schema))
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
}
