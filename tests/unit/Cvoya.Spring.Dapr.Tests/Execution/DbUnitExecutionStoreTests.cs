// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Catalog;
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
        // ADR-0038 amendment (#2634): the canonical persisted shape is
        // {runtime, model{provider, id}, image}.
        using var doc = JsonDocument.Parse("""
            {
              "execution": {
                "image": "ghcr.io/foo:latest",
                "model": { "provider": "ollama", "id": "llama3.2:3b" },
                "runtime": "claude-code"
              }
            }
            """);
        var defaults = DbUnitExecutionStore.Extract(doc.RootElement);
        defaults.ShouldNotBeNull();
        defaults!.Image.ShouldBe("ghcr.io/foo:latest");
        defaults.Model.ShouldBe(new Model("ollama", "llama3.2:3b"));
        defaults.Runtime.ShouldBe("claude-code");
    }

    [Fact]
    public void Extract_RuntimeSlot_OmittedWhenAbsent()
    {
        // A unit that declares only an image must keep returning the other
        // fields without tripping on the missing keys.
        using var doc = JsonDocument.Parse("""
            {
              "execution": {
                "image": "ghcr.io/foo:latest"
              }
            }
            """);
        var defaults = DbUnitExecutionStore.Extract(doc.RootElement);
        defaults.ShouldNotBeNull();
        defaults!.Image.ShouldBe("ghcr.io/foo:latest");
        defaults.Runtime.ShouldBeNull();
        defaults.Model.ShouldBeNull();
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
        // Runtime is a first-class slot in IsEmpty so a unit can declare
        // just `ai.runtime` without the other fields and still get its
        // execution block persisted.
        new UnitExecutionDefaults(Runtime: "claude-code").IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void UnitExecutionDefaults_IsEmpty_FalseWhenOnlySystemPromptModeSet()
    {
        // #2691 / #2692: system_prompt_mode is a first-class slot in
        // IsEmpty so the PUT /api/v1/units/{id}/execution path accepts a
        // body that carries only `systemPromptMode` (the all-null-other-
        // fields request must not be rejected with "must carry at least
        // one non-empty field").
        new UnitExecutionDefaults(SystemPromptMode: SystemPromptMode.Replace).IsEmpty.ShouldBeFalse();
    }

    [Fact]
    public void Extract_ReadsSystemPromptModeReplace()
    {
        // #2691: the lower-case wire literal round-trips through the JSON
        // shape onto the typed enum slot.
        using var doc = JsonDocument.Parse("""
            {
              "execution": {
                "runtime": "claude-code",
                "system_prompt_mode": "replace"
              }
            }
            """);
        var defaults = DbUnitExecutionStore.Extract(doc.RootElement);
        defaults.ShouldNotBeNull();
        defaults!.SystemPromptMode.ShouldBe(SystemPromptMode.Replace);
    }

    [Fact]
    public void Extract_ReadsSystemPromptModeAppend()
    {
        using var doc = JsonDocument.Parse("""
            {
              "execution": {
                "runtime": "claude-code",
                "system_prompt_mode": "APPEND"
              }
            }
            """);
        var defaults = DbUnitExecutionStore.Extract(doc.RootElement);
        defaults.ShouldNotBeNull();
        defaults!.SystemPromptMode.ShouldBe(SystemPromptMode.Append);
    }

    [Fact]
    public void Extract_UnknownSystemPromptModeLiteral_TreatsAsAbsent()
    {
        // The persisted JSON is supposed to have been validated at the API
        // boundary; an out-of-band write that carries an unknown literal
        // degrades to null rather than throwing — same tolerance as the
        // other extraction fields.
        using var doc = JsonDocument.Parse("""
            {
              "execution": {
                "runtime": "claude-code",
                "system_prompt_mode": "extend"
              }
            }
            """);
        var defaults = DbUnitExecutionStore.Extract(doc.RootElement);
        defaults.ShouldNotBeNull();
        defaults!.SystemPromptMode.ShouldBeNull();
        defaults.Runtime.ShouldBe("claude-code");
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
        // Write → read round-trip for the `runtime` slot. Uses an
        // in-memory DB because DbUnitExecutionStore drives EF Core directly.
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
    public async Task SetAsync_PartialUpdate_PreservesRuntime()
    {
        // Partial-update semantics: writing `Image` alone must not clobber
        // a previously-stored runtime / model value.
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
