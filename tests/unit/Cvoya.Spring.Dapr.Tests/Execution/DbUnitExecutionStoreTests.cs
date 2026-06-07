// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DbUnitExecutionStore"/>. The static
/// <see cref="DbUnitExecutionStore.ExtractJsonbSlots(JsonElement?)"/> helper
/// covers the jsonb-homed slots (image / runtime / system_prompt_mode); the
/// <c>SetAsync</c> / <c>GetAsync</c> round-trips cover the
/// ADR-0067 §2 (#3111) single home for <c>model</c> on
/// <c>unit_live_config</c>. The full DB integration path is also exercised by
/// the integration tests.
/// </summary>
public class DbUnitExecutionStoreTests
{
    [Fact]
    public void ExtractJsonbSlots_ReturnsDefault_WhenDefinitionIsMissing()
    {
        var slots = DbUnitExecutionStore.ExtractJsonbSlots(null);
        slots.Image.ShouldBeNull();
        slots.Runtime.ShouldBeNull();
        slots.SystemPromptMode.ShouldBeNull();
    }

    [Fact]
    public void ExtractJsonbSlots_ReturnsDefault_WhenNoExecutionBlock()
    {
        using var doc = JsonDocument.Parse("""{"instructions":"hi"}""");
        var slots = DbUnitExecutionStore.ExtractJsonbSlots(doc.RootElement);
        slots.Image.ShouldBeNull();
        slots.Runtime.ShouldBeNull();
    }

    [Fact]
    public void ExtractJsonbSlots_ReturnsImageAndRuntime_IgnoresModel()
    {
        // ADR-0067 §2 (#3111): model and hosting are NOT read from the jsonb —
        // the unit's model home is unit_live_config, hosting always lived
        // there. The jsonb carries only image / runtime / system_prompt_mode.
        using var doc = JsonDocument.Parse("""
            {
              "execution": {
                "image": "ghcr.io/foo:latest",
                "model": { "provider": "ollama", "id": "llama3.2:3b" },
                "hosting": "persistent",
                "runtime": "claude-code"
              }
            }
            """);
        var slots = DbUnitExecutionStore.ExtractJsonbSlots(doc.RootElement);
        slots.Image.ShouldBe("ghcr.io/foo:latest");
        slots.Runtime.ShouldBe("claude-code");
    }

    [Fact]
    public void ExtractJsonbSlots_RuntimeSlot_OmittedWhenAbsent()
    {
        using var doc = JsonDocument.Parse("""
            {
              "execution": {
                "image": "ghcr.io/foo:latest"
              }
            }
            """);
        var slots = DbUnitExecutionStore.ExtractJsonbSlots(doc.RootElement);
        slots.Image.ShouldBe("ghcr.io/foo:latest");
        slots.Runtime.ShouldBeNull();
    }

    [Fact]
    public void ExtractJsonbSlots_TrimsWhitespace()
    {
        using var doc = JsonDocument.Parse("""{"execution":{"image":"  ghcr.io/x  "}}""");
        var slots = DbUnitExecutionStore.ExtractJsonbSlots(doc.RootElement);
        slots.Image.ShouldBe("ghcr.io/x");
    }

    [Fact]
    public void UnitExecutionDefaults_IsEmpty_WhenAllFieldsNullOrBlank()
    {
        new UnitExecutionDefaults().IsEmpty.ShouldBeTrue();
        new UnitExecutionDefaults(Image: "  ").IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void UnitExecutionDefaults_IsEmpty_FalseWhenOneFieldSet()
    {
        new UnitExecutionDefaults(Image: "x").IsEmpty.ShouldBeFalse();
        new UnitExecutionDefaults(Model: new Model("ollama", "x")).IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void UnitExecutionDefaults_IsEmpty_FalseWhenOnlyRuntimeSet()
    {
        new UnitExecutionDefaults(Runtime: "claude-code").IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void UnitExecutionDefaults_IsEmpty_FalseWhenOnlySystemPromptModeSet()
    {
        new UnitExecutionDefaults(SystemPromptMode: SystemPromptMode.Replace).IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void ExtractJsonbSlots_ReadsSystemPromptModeReplace()
    {
        using var doc = JsonDocument.Parse("""
            {
              "execution": {
                "runtime": "claude-code",
                "system_prompt_mode": "replace"
              }
            }
            """);
        var slots = DbUnitExecutionStore.ExtractJsonbSlots(doc.RootElement);
        slots.SystemPromptMode.ShouldBe(SystemPromptMode.Replace);
    }

    [Fact]
    public void ExtractJsonbSlots_ReadsSystemPromptModeAppend()
    {
        using var doc = JsonDocument.Parse("""
            {
              "execution": {
                "runtime": "claude-code",
                "system_prompt_mode": "APPEND"
              }
            }
            """);
        var slots = DbUnitExecutionStore.ExtractJsonbSlots(doc.RootElement);
        slots.SystemPromptMode.ShouldBe(SystemPromptMode.Append);
    }

    [Fact]
    public void ExtractJsonbSlots_UnknownSystemPromptModeLiteral_TreatsAsAbsent()
    {
        using var doc = JsonDocument.Parse("""
            {
              "execution": {
                "runtime": "claude-code",
                "system_prompt_mode": "extend"
              }
            }
            """);
        var slots = DbUnitExecutionStore.ExtractJsonbSlots(doc.RootElement);
        slots.SystemPromptMode.ShouldBeNull();
        slots.Runtime.ShouldBe("claude-code");
    }

    [Fact]
    public async Task SetAsync_RoundTripsSystemPromptMode()
    {
        var actorGuid = Guid.NewGuid();
        var (store, _) = BuildStore(actorGuid);

        await store.SetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            new UnitExecutionDefaults(
                Runtime: "claude-code",
                SystemPromptMode: SystemPromptMode.Replace),
            TestContext.Current.CancellationToken);

        var read = await store.GetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            TestContext.Current.CancellationToken);

        read.ShouldNotBeNull();
        read!.SystemPromptMode.ShouldBe(SystemPromptMode.Replace);
        read.Runtime.ShouldBe("claude-code");
    }

    [Fact]
    public async Task SetAsync_RoundTripsRuntimeSlot()
    {
        var actorGuid = Guid.NewGuid();
        var (store, _) = BuildStore(actorGuid);

        await store.SetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            new UnitExecutionDefaults(Runtime: "claude-code"),
            TestContext.Current.CancellationToken);

        var read = await store.GetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            TestContext.Current.CancellationToken);

        read.ShouldNotBeNull();
        read!.Runtime.ShouldBe("claude-code");
    }

    [Fact]
    public async Task SetAsync_RoundTripsModel_ToUnitLiveConfig()
    {
        // ADR-0067 §2 (#3111): the model's single home is unit_live_config.
        // SetAsync persists the structured (provider, id) onto the live-config
        // {provider, model} columns; GetAsync lifts the pair back onto the
        // UnitExecutionDefaults.Model shape consumers (Merge / inheritance /
        // dispatch) read.
        var actorGuid = Guid.NewGuid();
        var (store, scopeFactory) = BuildStore(actorGuid);

        await store.SetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            new UnitExecutionDefaults(
                Runtime: "claude-code",
                Model: new Model("anthropic", "claude-opus-4-8")),
            TestContext.Current.CancellationToken);

        var read = await store.GetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            TestContext.Current.CancellationToken);

        read.ShouldNotBeNull();
        read!.Model.ShouldBe(new Model("anthropic", "claude-opus-4-8"));
        read.Runtime.ShouldBe("claude-code");

        // The model landed on unit_live_config, NOT the unit jsonb.
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var liveConfig = await db.UnitLiveConfigs.AsNoTracking()
            .FirstOrDefaultAsync(c => c.UnitId == actorGuid, TestContext.Current.CancellationToken);
        liveConfig.ShouldNotBeNull();
        liveConfig!.Provider.ShouldBe("anthropic");
        liveConfig.Model.ShouldBe("claude-opus-4-8");

        var unitDef = await db.UnitDefinitions.AsNoTracking()
            .FirstAsync(u => u.Id == actorGuid, TestContext.Current.CancellationToken);
        DbUnitExecutionStore.ExtractJsonbSlots(unitDef.Definition).Runtime.ShouldBe("claude-code");
        // The jsonb execution block carries no `model` key.
        unitDef.Definition!.Value.GetProperty("execution").TryGetProperty("model", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task SetAsync_PartialUpdate_PreservesRuntimeAndModel()
    {
        // Partial-update semantics: writing `Image` alone must not clobber a
        // previously-stored runtime (jsonb) or model (live-config) value.
        var actorGuid = Guid.NewGuid();
        var (store, _) = BuildStore(actorGuid);

        await store.SetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            new UnitExecutionDefaults(
                Runtime: "claude-code",
                Model: new Model("anthropic", "claude-opus-4-7")),
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
        read.Model.ShouldBe(new Model("anthropic", "claude-opus-4-7"));
        read.Runtime.ShouldBe("claude-code");
    }

    [Fact]
    public async Task ClearAsync_ClearsModel_AndJsonbBlock()
    {
        var actorGuid = Guid.NewGuid();
        var (store, _) = BuildStore(actorGuid);

        await store.SetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            new UnitExecutionDefaults(
                Runtime: "claude-code",
                Model: new Model("anthropic", "claude-opus-4-8")),
            TestContext.Current.CancellationToken);

        await store.ClearAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            TestContext.Current.CancellationToken);

        var read = await store.GetAsync(
            Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid),
            TestContext.Current.CancellationToken);

        read.ShouldBeNull();
    }

    private static (DbUnitExecutionStore Store, IServiceScopeFactory ScopeFactory) BuildStore(Guid unitActorId)
    {
        var dbName = $"unit-exec-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(OssTenantIds.Default));
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
                TenantId = OssTenantIds.Default,
                DisplayName = "test-unit",
                Description = "test",
            });
            db.SaveChanges();
        }

        var store = new DbUnitExecutionStore(scopeFactory, NullLoggerFactory.Instance);
        return (store, scopeFactory);
    }
}
