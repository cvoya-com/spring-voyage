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
/// End-to-end coverage for the <c>sv.memory_*</c> / <c>sv.topic_*</c>
/// platform tools (#2342). Wires the EF stores against an in-memory
/// SpringDbContext, registers the <see cref="SvMemorySkillRegistry"/>
/// with the same caller-scope contract the MCP server uses at runtime,
/// then drives a realistic capture → recall → cross-tool linkage flow
/// through the registry's <see cref="ISkillRegistry.InvokeAsync(string,
/// JsonElement, ToolCallContext, CancellationToken)"/> overload.
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
        services.AddSingleton<IMemoryTopicStore, EfMemoryTopicStore>();
        services.AddSingleton<SvMemorySkillRegistry>();
        services.AddSingleton<EfMemoryStore>(sp => new EfMemoryStore(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            NullLogger<EfMemoryStore>.Instance));
        services.AddSingleton<EfMemoryTopicStore>(sp => new EfMemoryTopicStore(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            NullLogger<EfMemoryTopicStore>.Instance));
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        _services = services.BuildServiceProvider();

        var memoryStore = new EfMemoryStore(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<TimeProvider>(),
            NullLogger<EfMemoryStore>.Instance);
        var topicStore = new EfMemoryTopicStore(
            _services.GetRequiredService<IServiceScopeFactory>(),
            _services.GetRequiredService<TimeProvider>(),
            NullLogger<EfMemoryTopicStore>.Instance);
        _registry = new SvMemorySkillRegistry(memoryStore, topicStore, NullLoggerFactory.Instance);
    }

    public void Dispose()
    {
        _services.Dispose();
    }

    [Fact]
    public async Task CaptureRecallAndLink_RoundTripsThroughTheToolSurface()
    {
        var ct = TestContext.Current.CancellationToken;

        var agentId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var caller = AgentCaller(agentId, threadId);

        // 1. Create a topic.
        var topicJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.TopicAddTool,
            ParseArgs("""{"name":"design-decisions","description":"the why behind the what"}"""),
            caller, ct);
        var topicId = ReadGuid(topicJson, "id");

        // 2. Capture a long-term memory linked to the topic.
        var addJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryAddTool,
            ParseArgs($$"""{"content":"opted for actor-state over EF for hot mailbox path","kind":"long_term","topic_ids":["{{topicId:N}}"]}"""),
            caller, ct);
        var memoryId = ReadGuid(addJson, "id");

        // 3. Capture a short-term thread note.
        await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryAddTool,
            ParseArgs("""{"content":"the user asked me to check the design rationale","kind":"short_term"}"""),
            caller, ct);

        // 4. List long-term filtered by topic — should return our linked entry only.
        var listJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryListTool,
            ParseArgs($$"""{"kind":"long_term","topic_id":"{{topicId:N}}"}"""),
            caller, ct);
        listJson.GetProperty("memories").GetArrayLength().ShouldBe(1);
        ReadGuid(listJson.GetProperty("memories")[0], "id").ShouldBe(memoryId);

        // 5. Free-text search finds the long-term entry.
        var searchJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemorySearchTool,
            ParseArgs("""{"query":"actor-state"}"""),
            caller, ct);
        searchJson.GetArrayLength().ShouldBe(1);
        ReadGuid(searchJson[0], "id").ShouldBe(memoryId);

        // 6. Topic listing shows the one topic.
        var topicListJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.TopicListTool,
            ParseArgs("""{}"""),
            caller, ct);
        topicListJson.GetProperty("topics").GetArrayLength().ShouldBe(1);

        // 7. Deleting the topic cascades the link but keeps the memory.
        var deleteJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.TopicDeleteTool,
            ParseArgs($$"""{"id":"{{topicId:N}}"}"""),
            caller, ct);
        deleteJson.GetProperty("deleted").GetBoolean().ShouldBeTrue();

        var afterDeleteMemoryJson = await _registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryGetTool,
            ParseArgs($$"""{"id":"{{memoryId:N}}"}"""),
            caller, ct);
        afterDeleteMemoryJson.ValueKind.ShouldBe(JsonValueKind.Object);
        afterDeleteMemoryJson.GetProperty("topic_ids").GetArrayLength().ShouldBe(0);
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
            ParseArgs("""{"content":"private to agent A","kind":"long_term"}"""),
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
