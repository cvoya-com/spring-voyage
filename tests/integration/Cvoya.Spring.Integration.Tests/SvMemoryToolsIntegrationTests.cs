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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end coverage for the <c>sv.memory.*</c> platform tools
/// (#2342). Wires the EF store against an in-memory SpringDbContext,
/// registers the <see cref="SvMemorySkillRegistry"/> with the same
/// caller-scope contract the MCP server uses at runtime, then drives a
/// realistic capture → recall flow through the registry's
/// <see cref="ISkillRegistry.InvokeAsync(string, JsonElement,
/// ToolCallContext, CancellationToken)"/> overload.
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
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        _services = services.BuildServiceProvider();

        var memoryStore = new EfMemoryStore(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<TimeProvider>(),
            NullLogger<EfMemoryStore>.Instance);
        _registry = new SvMemorySkillRegistry(memoryStore, NullLoggerFactory.Instance);
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
        var threadId = Guid.NewGuid();
        var caller = AgentCaller(agentId, threadId);

        // 1. Capture an agent-scoped memory.
        var addJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryAddTool,
            ParseArgs("""{"content":"opted for actor-state over EF for hot mailbox path","scope":"agent"}"""),
            caller, ct);
        var memoryId = ReadGuid(addJson, "id");

        // 2. Capture a thread-scoped note bound to the caller's thread.
        await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryAddTool,
            ParseArgs("""{"content":"the user asked me to check the design rationale","scope":"thread"}"""),
            caller, ct);

        // 3. List agent-scoped only — should return our agent-scoped entry.
        var listJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryListTool,
            ParseArgs("""{"scope":"agent"}"""),
            caller, ct);
        listJson.GetProperty("memories").GetArrayLength().ShouldBe(1);
        ReadGuid(listJson.GetProperty("memories")[0], "id").ShouldBe(memoryId);

        // 4. Free-text search finds the long-term entry.
        var searchJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemorySearchTool,
            ParseArgs("""{"query":"actor-state"}"""),
            caller, ct);
        searchJson.GetArrayLength().ShouldBe(1);
        ReadGuid(searchJson[0], "id").ShouldBe(memoryId);

        // 5. Update the entry's content; the response reflects the new text.
        var updateJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryUpdateTool,
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
        var callerA = AgentCaller(agentA, Guid.NewGuid());
        var callerB = AgentCaller(agentB, Guid.NewGuid());

        var addJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryAddTool,
            ParseArgs("""{"content":"private to agent A","scope":"agent"}"""),
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
        var caller = AgentCaller(Guid.NewGuid(), Guid.NewGuid());

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

        // 4. update can replace structured content with a plain text note.
        var updateJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryUpdateTool,
            ParseArgs($$"""{"id":"{{memoryId:N}}","content":"archived"}"""),
            caller, ct);
        updateJson.GetProperty("content").ValueKind.ShouldBe(JsonValueKind.String);
        updateJson.GetProperty("content").GetString().ShouldBe("archived");
    }

    [Fact]
    public async Task ThreadScopedRecall_OnlySurfacesWithinItsOwnThread()
    {
        // #2997 recall filter end-to-end: an agent-scoped fact is recalled
        // in every conversation, but a thread-scoped note is recalled only
        // inside the thread it was captured in.
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        var threadX = Guid.NewGuid();
        var threadY = Guid.NewGuid();
        var callerInX = AgentCaller(agentId, threadX);
        var callerInY = AgentCaller(agentId, threadY);

        await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryAddTool,
            ParseArgs("""{"content":"durable cross-thread fact","scope":"agent"}"""),
            callerInX, ct);
        await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryAddTool,
            ParseArgs("""{"content":"in this thread call me Bob","scope":"thread"}"""),
            callerInX, ct);

        // Within thread X: the agent-scoped fact and X's private note.
        var inX = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryListTool, ParseArgs("{}"), callerInX, ct);
        inX.GetProperty("memories").GetArrayLength().ShouldBe(2);

        // Within thread Y: only the agent-scoped fact — X's note is hidden.
        var inY = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryListTool, ParseArgs("{}"), callerInY, ct);
        inY.GetProperty("memories").GetArrayLength().ShouldBe(1);
        inY.GetProperty("memories")[0].GetProperty("content").GetString()
            .ShouldBe("durable cross-thread fact");

        // Search obeys the same recall filter: the "Bob" note surfaces only
        // inside thread X.
        var searchInY = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemorySearchTool, ParseArgs("""{"query":"Bob"}"""), callerInY, ct);
        searchInY.GetArrayLength().ShouldBe(0);

        var searchInX = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemorySearchTool, ParseArgs("""{"query":"Bob"}"""), callerInX, ct);
        searchInX.GetArrayLength().ShouldBe(1);
    }

    private static ToolCallContext AgentCaller(Guid agentId, Guid threadId) =>
        new(GuidFormatter.Format(agentId), Address.AgentScheme, GuidFormatter.Format(threadId));

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
