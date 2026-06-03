// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Memory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Contract + dispatch tests for <see cref="SvMemorySkillRegistry"/>
/// (#2342). Pin the tool surface, the no-context overload rejection,
/// the unknown-tool rejection, and the caller-scoping behaviour: every
/// store call receives the caller's address as the owner. Wire-shape
/// regression coverage for tool argument parsing is included for the
/// load-bearing tool (sv.memory.add) — JSON-content handling (#2991:
/// content crosses as a <see cref="JsonElement"/> and round-trips
/// natively) plus the scope axis and thread recall filter (#2997).
/// </summary>
public class SvMemorySkillRegistryTests
{
    private readonly IMemoryStore _memoryStore = Substitute.For<IMemoryStore>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    public SvMemorySkillRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
    }

    private SvMemorySkillRegistry CreateRegistry() => new(_memoryStore, _loggerFactory);

    private static ToolCallContext AgentContext(Guid callerId, Guid? threadId = null) =>
        new(
            CallerId: GuidFormatter.Format(callerId),
            CallerKind: Address.AgentScheme,
            ThreadId: threadId is { } t ? GuidFormatter.Format(t) : Guid.NewGuid().ToString("N"));

    // Echoes the AddAsync arguments back as a persisted entry so tests can
    // assert on the owner / content / thread binding the registry derived.
    // content (arg index 1) crosses as a JsonElement (#2991); scope is
    // derived from the thread binding (#2997).
    private void StubAddEcho() =>
        _memoryStore.AddAsync(Arg.Any<Address>(), Arg.Any<JsonElement>(), Arg.Any<string?>(),
                Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new MemoryEntry(
                Guid.NewGuid(),
                call.Arg<Address>(),
                call.ArgAt<JsonElement>(1),
                call.ArgAt<string?>(2),
                call.ArgAt<Guid?>(3),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)));

    [Fact]
    public void GetToolDefinitions_AdvertisesAllSixTools()
    {
        var registry = CreateRegistry();
        registry.GetToolDefinitions().Select(t => t.Name).ShouldBe(new[]
        {
            SvMemorySkillRegistry.MemoryAddTool,
            SvMemorySkillRegistry.MemoryGetTool,
            SvMemorySkillRegistry.MemoryListTool,
            SvMemorySkillRegistry.MemorySearchTool,
            SvMemorySkillRegistry.MemoryUpdateTool,
            SvMemorySkillRegistry.MemoryDeleteTool,
        });
    }

    [Fact]
    public void Name_IsSv()
    {
        CreateRegistry().Name.ShouldBe("sv");
    }

    [Fact]
    public async Task NoContextOverload_AlwaysThrows()
    {
        var registry = CreateRegistry();
        var args = JsonDocument.Parse("{}").RootElement;
        await Should.ThrowAsync<SpringException>(async () =>
            await registry.InvokeAsync(SvMemorySkillRegistry.MemoryListTool, args, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RichInvokeAsync_UnknownTool_ThrowsSkillNotFound()
    {
        var registry = CreateRegistry();
        var args = JsonDocument.Parse("{}").RootElement;
        await Should.ThrowAsync<SkillNotFoundException>(async () =>
            await registry.InvokeAsync("sv.does_not_exist", args, AgentContext(Guid.NewGuid()),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MemoryAdd_PassesCallerAddressAsOwner_AndDefaultsScopeToAgent()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var ctx = AgentContext(callerId);
        var args = JsonDocument.Parse("""{"content":"a note"}""").RootElement;
        StubAddEcho();

        await registry.InvokeAsync(SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken);

        // Default scope is agent → no thread binding.
        await _memoryStore.Received(1).AddAsync(
            Arg.Is<Address>(a => a.Scheme == Address.AgentScheme && a.Id == callerId),
            Arg.Is<JsonElement>(c => c.ValueKind == JsonValueKind.String && c.GetString() == "a note"),
            null,
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryAdd_StructuredContent_PassesJsonObjectToStore_AndEmitsItNatively()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse(
            """{"content":{"status":"published","piece":3}}""").RootElement;
        StubAddEcho();

        var result = await registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken);

        // The store receives the content as a JSON object, not a string.
        await _memoryStore.Received(1).AddAsync(
            Arg.Any<Address>(),
            Arg.Is<JsonElement>(c => c.ValueKind == JsonValueKind.Object
                && c.GetProperty("status").GetString() == "published"),
            null,
            null,
            Arg.Any<CancellationToken>());

        // The response emits content natively (an object), not a stringified blob.
        var content = result.GetProperty("content");
        content.ValueKind.ShouldBe(JsonValueKind.Object);
        content.GetProperty("piece").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task MemoryAdd_TextContent_EmitsString()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse("""{"content":"plain note"}""").RootElement;
        StubAddEcho();

        var result = await registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken);

        var content = result.GetProperty("content");
        content.ValueKind.ShouldBe(JsonValueKind.String);
        content.GetString().ShouldBe("plain note");
    }

    [Fact]
    public async Task MemoryAdd_AgentScoped_IgnoresExplicitThreadId()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var ctx = AgentContext(callerId, Guid.NewGuid());
        var args = JsonDocument.Parse(
            $$"""{"content":"x","scope":"agent","thread_id":"{{Guid.NewGuid().ToString("N")}}"}""")
            .RootElement;
        StubAddEcho();

        await registry.InvokeAsync(SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken);

        // Agent scope discards any supplied thread binding.
        await _memoryStore.Received(1).AddAsync(
            Arg.Any<Address>(),
            Arg.Is<JsonElement>(c => c.ValueKind == JsonValueKind.String && c.GetString() == "x"),
            null,
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryAdd_ThreadScoped_DefaultsToCallerThreadId()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var ctx = AgentContext(callerId, threadId);
        var args = JsonDocument.Parse("""{"content":"working","scope":"thread"}""").RootElement;
        StubAddEcho();

        await registry.InvokeAsync(SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken);

        await _memoryStore.Received(1).AddAsync(
            Arg.Any<Address>(),
            Arg.Is<JsonElement>(c => c.ValueKind == JsonValueKind.String && c.GetString() == "working"),
            null,
            threadId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryAdd_ThreadScoped_ExplicitThreadId_OverridesDefault()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var override_ = Guid.NewGuid();
        var ctx = AgentContext(callerId, threadId);
        var args = JsonDocument.Parse(
            $$"""{"content":"x","scope":"thread","thread_id":"{{override_.ToString("N")}}"}""")
            .RootElement;
        StubAddEcho();

        await registry.InvokeAsync(SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken);

        await _memoryStore.Received(1).AddAsync(
            Arg.Any<Address>(),
            Arg.Any<JsonElement>(),
            Arg.Any<string?>(),
            override_,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryList_PassesScopePaging_AndCallerThreadAsRecallThread()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var ctx = AgentContext(callerId, threadId);
        var args = JsonDocument.Parse(
            """{"scope":"thread","limit":20,"offset":5}""")
            .RootElement;
        _memoryStore.ListAsync(Arg.Any<Address>(), Arg.Any<MemoryScope?>(), Arg.Any<Guid?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>()));

        await registry.InvokeAsync(SvMemorySkillRegistry.MemoryListTool, args, ctx, TestContext.Current.CancellationToken);

        // The caller's active thread is threaded through as the recall
        // filter so only this conversation's thread-scoped notes surface.
        await _memoryStore.Received(1).ListAsync(
            Arg.Is<Address>(a => a.Id == callerId),
            MemoryScope.Thread,
            threadId,
            20,
            5,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemorySearch_PassesQueryCallerOwner_AndRecallThread()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var ctx = AgentContext(callerId, threadId);
        var args = JsonDocument.Parse("""{"query":"react hooks","limit":5}""").RootElement;
        _memoryStore.SearchAsync(Arg.Any<Address>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>()));

        await registry.InvokeAsync(SvMemorySkillRegistry.MemorySearchTool, args, ctx, TestContext.Current.CancellationToken);

        await _memoryStore.Received(1).SearchAsync(
            Arg.Is<Address>(a => a.Id == callerId),
            "react hooks",
            null,
            threadId,
            5,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryUpdate_StructuredContent_PassesJsonObjectToStore()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var ctx = AgentContext(callerId);
        var args = JsonDocument.Parse(
            $$$"""{"id":"{{{id.ToString("N")}}}","content":{"phase":"done"}}""").RootElement;
        _memoryStore.UpdateAsync(Arg.Any<Address>(), Arg.Any<Guid>(),
                Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<MemoryEntry?>(new MemoryEntry(
                id, call.Arg<Address>(),
                call.ArgAt<JsonElement?>(2)!.Value, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

        var result = await registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryUpdateTool, args, ctx, TestContext.Current.CancellationToken);

        await _memoryStore.Received(1).UpdateAsync(
            Arg.Is<Address>(a => a.Id == callerId),
            id,
            Arg.Is<JsonElement?>(c => c.HasValue && c.Value.ValueKind == JsonValueKind.Object),
            Arg.Any<CancellationToken>());
        result.GetProperty("content").ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task MemoryDelete_ReturnsDeletedFlag()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var ctx = AgentContext(callerId);
        var args = JsonDocument.Parse($$"""{"id":"{{id.ToString("N")}}"}""").RootElement;
        _memoryStore.DeleteAsync(Arg.Any<Address>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var result = await registry.InvokeAsync(SvMemorySkillRegistry.MemoryDeleteTool, args, ctx, TestContext.Current.CancellationToken);
        result.GetProperty("deleted").GetBoolean().ShouldBeTrue();

        await _memoryStore.Received(1).DeleteAsync(
            Arg.Is<Address>(a => a.Id == callerId), id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryUpdate_MissingEntry_ReturnsCleanNotFound()
    {
        // #3036: a stale / unknown id is a self-correctable condition, not a
        // platform fault. The tool returns a clean { updated: false, reason:
        // "not_found", id } with isError=false instead of throwing a
        // SpringException the model would read as a crash.
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var id = Guid.NewGuid();
        var args = JsonDocument.Parse($$"""{"id":"{{id.ToString("N")}}","content":"x"}""").RootElement;
        _memoryStore.UpdateAsync(Arg.Any<Address>(), Arg.Any<Guid>(),
                Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntry?>(null));

        var result = await registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryUpdateTool, args, ctx, TestContext.Current.CancellationToken);

        result.GetProperty("updated").GetBoolean().ShouldBeFalse();
        result.GetProperty("reason").GetString().ShouldBe("not_found");
        result.GetProperty("id").GetString().ShouldBe(GuidFormatter.Format(id));
    }

    [Fact]
    public async Task MemoryUpdate_MissingId_ThrowsRetryGuidingArgumentException()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse("""{"content":"x"}""").RootElement;

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(SvMemorySkillRegistry.MemoryUpdateTool, args, ctx, TestContext.Current.CancellationToken));

        // The error names what to pass and where a fresh id comes from.
        ex.Message.ShouldContain("id");
        ex.Message.ShouldContain("sv.memory.list");
    }

    [Fact]
    public async Task MemoryGet_NotFound_ReturnsJsonNull()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var id = Guid.NewGuid();
        var args = JsonDocument.Parse($$"""{"id":"{{id.ToString("N")}}"}""").RootElement;
        _memoryStore.GetAsync(Arg.Any<Address>(), id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntry?>(null));

        var result = await registry.InvokeAsync(SvMemorySkillRegistry.MemoryGetTool, args, ctx, TestContext.Current.CancellationToken);
        result.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task MemoryAdd_MissingContent_ThrowsArgumentException()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse("""{}""").RootElement;

        await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MemoryAdd_JsonNullContent_ThrowsArgumentException()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse("""{"content":null}""").RootElement;

        await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MemoryAdd_EmptyStringContent_ThrowsArgumentException()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse("""{"content":"   "}""").RootElement;

        await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken));
    }
}
