// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the static extraction helper on
/// <see cref="DbUnitExecutionStore.Extract(JsonElement?)"/>. The DB
/// integration path is exercised indirectly via the integration tests.
/// </summary>
public class DbUnitExecutionStoreTests
{
    [Fact]
    public void Extract_ReturnsNull_WhenDefinitionIsMissing()
    {
        DbUnitExecutionStore.Extract(null).ShouldBeNull();
    }

    [Fact]
    public void Extract_ReturnsNull_WhenNoExecutionBlock()
    {
        using var doc = JsonDocument.Parse("""{"instructions":"hi"}""");
        DbUnitExecutionStore.Extract(doc.RootElement).ShouldBeNull();
    }

    [Fact]
    public void Extract_ReturnsAllFields()
    {
        using var doc = JsonDocument.Parse("""
            {
              "execution": {
                "image": "ghcr.io/foo:latest",
                "runtime": "podman",
                "tool": "dapr-agent",
                "provider": "ollama",
                "model": "llama3.2:3b",
                "agent": "claude"
              }
            }
            """);
        var defaults = DbUnitExecutionStore.Extract(doc.RootElement);
        defaults.ShouldNotBeNull();
        defaults!.Image.ShouldBe("ghcr.io/foo:latest");
        defaults.Runtime.ShouldBe("podman");
        defaults.Tool.ShouldBe("dapr-agent");
        defaults.Provider.ShouldBe("ollama");
        defaults.Model.ShouldBe("llama3.2:3b");
        defaults.Agent.ShouldBe("claude");
    }

    [Fact]
    public void Extract_AgentSlot_OmittedWhenAbsent()
    {
        // Back-compat: pre-#1683 manifests never persisted the `agent`
        // slot. Extract must keep returning the other fields without
        // tripping on the missing key.
        using var doc = JsonDocument.Parse("""
            {
              "execution": {
                "image": "ghcr.io/foo:latest",
                "runtime": "podman"
              }
            }
            """);
        var defaults = DbUnitExecutionStore.Extract(doc.RootElement);
        defaults.ShouldNotBeNull();
        defaults!.Image.ShouldBe("ghcr.io/foo:latest");
        defaults.Runtime.ShouldBe("podman");
        defaults.Agent.ShouldBeNull();
    }

    [Fact]
    public void Extract_ReturnsNull_WhenBlockIsEmptyObject()
    {
        using var doc = JsonDocument.Parse("""{"execution":{}}""");
        DbUnitExecutionStore.Extract(doc.RootElement).ShouldBeNull();
    }

    [Fact]
    public void Extract_TrimsWhitespace()
    {
        using var doc = JsonDocument.Parse("""{"execution":{"image":"  ghcr.io/x  "}}""");
        var defaults = DbUnitExecutionStore.Extract(doc.RootElement);
        defaults.ShouldNotBeNull();
        defaults!.Image.ShouldBe("ghcr.io/x");
    }

    [Fact]
    public void UnitExecutionDefaults_IsEmpty_WhenAllFieldsNullOrBlank()
    {
        new UnitExecutionDefaults().IsEmpty.ShouldBeTrue();
        new UnitExecutionDefaults(Image: "  ", Runtime: null).IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void UnitExecutionDefaults_IsEmpty_FalseWhenOneFieldSet()
    {
        new UnitExecutionDefaults(Image: "x").IsEmpty.ShouldBeFalse();
        new UnitExecutionDefaults(Model: "x").IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void UnitExecutionDefaults_IsEmpty_FalseWhenOnlyAgentSet()
    {
        // #1683: agent is a first-class slot in IsEmpty so a unit can
        // declare just `ai.agent` without the other fields and still
        // get its execution block persisted.
        new UnitExecutionDefaults(Agent: "claude").IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public async Task SetAsync_RoundTripsAgentSlot()
    {
        // #1683: write → read round-trip for the new `agent` slot.
        // Mirrors the pattern of the other slot tests; uses an in-memory
        // DB because DbUnitExecutionStore drives EF Core directly.
        var actorGuid = Guid.NewGuid();
        var (store, _) = BuildStore(actorGuid);

        await store.SetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            new UnitExecutionDefaults(Agent: "claude"),
            TestContext.Current.CancellationToken);

        var read = await store.GetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            TestContext.Current.CancellationToken);

        read.ShouldNotBeNull();
        read!.Agent.ShouldBe("claude");
    }

    [Fact]
    public async Task SetAsync_PartialUpdate_PreservesAgent()
    {
        // Partial-update semantics extend to the new agent slot:
        // writing `Image` alone must not clobber a previously-stored
        // agent value.
        var actorGuid = Guid.NewGuid();
        var (store, _) = BuildStore(actorGuid);

        await store.SetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            new UnitExecutionDefaults(Agent: "claude", Runtime: "podman"),
            TestContext.Current.CancellationToken);

        await store.SetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            new UnitExecutionDefaults(Image: "ghcr.io/foo:latest"),
            TestContext.Current.CancellationToken);

        var read = await store.GetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            TestContext.Current.CancellationToken);

        read.ShouldNotBeNull();
        read!.Image.ShouldBe("ghcr.io/foo:latest");
        read.Runtime.ShouldBe("podman");
        read.Agent.ShouldBe("claude");
    }

    private static (DbUnitExecutionStore Store, IServiceScopeFactory ScopeFactory) BuildStore(Guid unitActorId)
    {
        var dbName = $"unit-exec-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

        // Pre-seed a UnitDefinitionEntity row keyed by the actor Guid the
        // store will look up. Production-side this row is upserted by the
        // directory service; the store does not own its lifecycle.
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = unitActorId,
                DisplayName = "test-unit",
                Description = "test",
            });
            db.SaveChanges();
        }

        var store = new DbUnitExecutionStore(scopeFactory, NullLoggerFactory.Instance);
        return (store, scopeFactory);
    }
}