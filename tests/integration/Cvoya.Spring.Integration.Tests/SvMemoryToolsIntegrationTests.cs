// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Memory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Memory;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Dapr.Threads;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end coverage for the durable-store <c>sv.memory.*</c> tools
/// (#2342, reshaped by #3038 / #3041 Part A). Wires the EF store and a
/// real <see cref="EfThreadRegistry"/> against an in-memory
/// SpringDbContext, registers the <see cref="SvMemorySkillRegistry"/> with
/// the same caller-scope contract the MCP server uses at runtime, then
/// drives a realistic capture → recall flow through the registry's
/// caller-aware <see cref="ISkillRegistry.InvokeAsync(string, JsonElement,
/// ToolCallContext, CancellationToken)"/> overload — exercising both the
/// object-primary / text content split and the participant-set
/// conversation model against the real participant-key resolution.
/// </summary>
public sealed class SvMemoryToolsIntegrationTests : IDisposable
{
    private static readonly Guid TenantId = new("dd55c4ea-8d72-5e43-a9df-88d07af02b69");

    private readonly ServiceProvider _services;
    private readonly SvMemorySkillRegistry _registry;

    public SvMemoryToolsIntegrationTests()
    {
        var dbName = $"sv_memory_integration_{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantId));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IMemoryStore, EfMemoryStore>();
        services.AddSingleton<SvMemorySkillRegistry>();
        services.AddSingleton<EfMemoryStore>(sp => new EfMemoryStore(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            NullLogger<EfMemoryStore>.Instance));
        // Real participant-key resolution: sv.memory.* maps an agent's
        // `participants` to the internal conversation binding through the
        // same registry sv.memory.history_with uses.
        services.AddScoped<IThreadRegistry, EfThreadRegistry>();
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        _services = services.BuildServiceProvider();

        var memoryStore = new EfMemoryStore(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<TimeProvider>(),
            NullLogger<EfMemoryStore>.Instance);
        _registry = new SvMemorySkillRegistry(
            memoryStore,
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLoggerFactory.Instance);
    }

    public void Dispose()
    {
        _services.Dispose();
    }

    [Fact]
    public async Task CaptureRecallAndUpdate_RoundTripsThroughTheToolSurface()
    {
        var ct = TestContext.Current.CancellationToken;

        var agentId = Guid.NewGuid();
        var caller = AgentCaller(agentId);
        var peer = new Address(Address.AgentScheme, Guid.NewGuid()).ToString();

        // 1. Capture an agent-wide text note.
        var addJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryTextAddTool,
            ParseArgs("""{"content":"opted for actor-state over EF for hot mailbox path"}"""),
            caller, ct);
        var memoryId = ReadGuid(addJson, "id");

        // 2. Capture a conversation-scoped note tied to a participant set.
        await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryTextAddTool,
            ParseArgs($$"""{"content":"the user asked me to check the design rationale","participants":["{{peer}}"]}"""),
            caller, ct);

        // 3. List agent-wide (no participants) — only the agent-wide entry.
        var listJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryListTool,
            ParseArgs("{}"),
            caller, ct);
        listJson.GetProperty("memories").GetArrayLength().ShouldBe(1);
        ReadGuid(listJson.GetProperty("memories")[0], "id").ShouldBe(memoryId);

        // 4. Free-text search (agent-wide) finds the entry.
        var searchJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemorySearchTool,
            ParseArgs("""{"query":"actor-state"}"""),
            caller, ct);
        searchJson.GetArrayLength().ShouldBe(1);
        ReadGuid(searchJson[0], "id").ShouldBe(memoryId);

        // 5. Update the entry's content via the text variant.
        var updateJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryTextUpdateTool,
            ParseArgs($$"""{"id":"{{memoryId:N}}","content":"hot path stayed on actor state per ADR-0040"}"""),
            caller, ct);
        updateJson.GetProperty("content").GetString().ShouldBe("hot path stayed on actor state per ADR-0040");

        // 6. Deleting the entry returns deleted:true; the subsequent get is null.
        var deleteJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryDeleteTool,
            ParseArgs($$"""{"id":"{{memoryId:N}}"}"""),
            caller, ct);
        deleteJson.GetProperty("deleted").GetBoolean().ShouldBeTrue();

        var afterDeleteJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryGetTool,
            ParseArgs($$"""{"id":"{{memoryId:N}}"}"""),
            caller, ct);
        afterDeleteJson.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task CrossOwnerIsolation_AgentBCannotReadAgentAEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();
        var callerA = AgentCaller(agentA);
        var callerB = AgentCaller(agentB);

        var addJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryTextAddTool,
            ParseArgs("""{"content":"private to agent A"}"""),
            callerA, ct);
        var id = ReadGuid(addJson, "id");

        // Agent B sees null on direct get and empty list / search.
        var getJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryGetTool,
            ParseArgs($$"""{"id":"{{id:N}}"}"""),
            callerB, ct);
        getJson.ValueKind.ShouldBe(JsonValueKind.Null);

        var searchJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemorySearchTool,
            ParseArgs("""{"query":"agent A"}"""),
            callerB, ct);
        searchJson.GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task StructuredJsonContent_RoundTripsThroughTheToolSurface()
    {
        var ct = TestContext.Current.CancellationToken;
        var caller = AgentCaller(Guid.NewGuid());

        // 1. Capture a structured JSON memory (an "edition status board").
        var addJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryAddTool,
            ParseArgs("""{"content":{"piece":3,"status":"published","headline":"Spring arrives"}}"""),
            caller, ct);
        var memoryId = ReadGuid(addJson, "id");

        // content comes back as a native object, not a stringified blob.
        var added = addJson.GetProperty("content");
        added.ValueKind.ShouldBe(JsonValueKind.Object);
        added.GetProperty("status").GetString().ShouldBe("published");

        // 2. get round-trips the object with its structure intact.
        var getJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryGetTool,
            ParseArgs($$"""{"id":"{{memoryId:N}}"}"""),
            caller, ct);
        getJson.GetProperty("content").GetProperty("headline").GetString()
            .ShouldBe("Spring arrives");

        // 3. search finds it by a contained value and returns native JSON.
        var searchJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemorySearchTool,
            ParseArgs("""{"query":"published"}"""),
            caller, ct);
        searchJson.GetArrayLength().ShouldBe(1);
        searchJson[0].GetProperty("content").ValueKind.ShouldBe(JsonValueKind.Object);

        // 4. the text update variant can replace structured content with a
        //    plain-text note (the JSON type may change across an update).
        var updateJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryTextUpdateTool,
            ParseArgs($$"""{"id":"{{memoryId:N}}","content":"archived"}"""),
            caller, ct);
        updateJson.GetProperty("content").ValueKind.ShouldBe(JsonValueKind.String);
        updateJson.GetProperty("content").GetString().ShouldBe("archived");
    }

    [Fact]
    public async Task ConversationMemory_IsPartitionedByParticipantSet()
    {
        // #3041 Part A end-to-end: agent-wide and per-conversation memory are
        // separate buckets, addressed by the presence/absence of
        // `participants`. A conversation's entries are isolated to that
        // participant set and never leak into the agent-wide bucket or into a
        // different conversation.
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        var caller = AgentCaller(agentId);
        var peerX = new Address(Address.HumanScheme, Guid.NewGuid()).ToString();
        var peerY = new Address(Address.HumanScheme, Guid.NewGuid()).ToString();

        await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryTextAddTool,
            ParseArgs("""{"content":"durable cross-conversation fact"}"""),
            caller, ct);
        await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryTextAddTool,
            ParseArgs($$"""{"content":"in this conversation call me Bob","participants":["{{peerX}}"]}"""),
            caller, ct);

        // Agent-wide bucket: only the agent-wide fact (the conversation note
        // is not merged in — the clean-partition recall model).
        var agentWide = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryListTool, ParseArgs("{}"), caller, ct);
        agentWide.GetProperty("memories").GetArrayLength().ShouldBe(1);
        agentWide.GetProperty("memories")[0].GetProperty("content").GetString()
            .ShouldBe("durable cross-conversation fact");

        // Conversation X's bucket: only X's private note.
        var inX = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryListTool,
            ParseArgs($$"""{"participants":["{{peerX}}"]}"""), caller, ct);
        inX.GetProperty("memories").GetArrayLength().ShouldBe(1);
        inX.GetProperty("memories")[0].GetProperty("content").GetString()
            .ShouldBe("in this conversation call me Bob");

        // A different conversation sees none of X's entries.
        var inY = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryListTool,
            ParseArgs($$"""{"participants":["{{peerY}}"]}"""), caller, ct);
        inY.GetProperty("memories").GetArrayLength().ShouldBe(0);

        // Search obeys the same partition: "Bob" surfaces only inside
        // conversation X, never agent-wide.
        var searchAgentWide = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemorySearchTool, ParseArgs("""{"query":"Bob"}"""), caller, ct);
        searchAgentWide.GetArrayLength().ShouldBe(0);

        var searchInX = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemorySearchTool,
            ParseArgs($$"""{"query":"Bob","participants":["{{peerX}}"]}"""), caller, ct);
        searchInX.GetArrayLength().ShouldBe(1);
    }

    private static ToolCallContext AgentCaller(Guid agentId) =>
        new(GuidFormatter.Format(agentId), Address.AgentScheme, GuidFormatter.Format(Guid.NewGuid()));

    private static JsonElement ParseArgs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Guid ReadGuid(JsonElement element, string property)
    {
        var raw = element.GetProperty(property).GetString();
        return GuidFormatter.TryParse(raw, out var guid)
            ? guid
            : throw new InvalidOperationException($"Expected Guid string at '{property}'; got '{raw}'.");
    }
}
