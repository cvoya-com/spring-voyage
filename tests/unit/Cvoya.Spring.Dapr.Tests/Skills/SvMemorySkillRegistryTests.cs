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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Contract + dispatch tests for <see cref="SvMemorySkillRegistry"/>
/// (#2342, reshaped by #3038 / #3041 Part A). Pin the tool surface, the
/// no-context overload rejection, the unknown-tool rejection, and the
/// caller-scoping behaviour. Wire-shape regression coverage for argument
/// parsing covers the typed content split (object-primary
/// <c>sv.memory.add</c> + text <c>sv.memory.text.add</c>) and the
/// participant-set conversation model (no <c>scope</c> / <c>thread_id</c>
/// on the wire — an optional <c>participants</c> resolves to the internal
/// conversation binding through <see cref="IThreadRegistry"/>).
/// </summary>
public class SvMemorySkillRegistryTests
{
    private readonly IMemoryStore _memoryStore = Substitute.For<IMemoryStore>();
    private readonly IThreadRegistry _threadRegistry = Substitute.For<IThreadRegistry>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    public SvMemorySkillRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
    }

    private SvMemorySkillRegistry CreateRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_threadRegistry);
        var sp = services.BuildServiceProvider();
        return new SvMemorySkillRegistry(
            _memoryStore,
            sp.GetRequiredService<IServiceScopeFactory>(),
            _loggerFactory);
    }

    private static ToolCallContext AgentContext(Guid callerId, Guid? threadId = null) =>
        new(
            CallerId: GuidFormatter.Format(callerId),
            CallerKind: Address.AgentScheme,
            ThreadId: threadId is { } t ? GuidFormatter.Format(t) : Guid.NewGuid().ToString("N"));

    // Echoes the AddAsync arguments back as a persisted entry so tests can
    // assert on the owner / content / conversation binding the registry
    // derived. content (arg index 1) crosses as a JsonElement (#2991);
    // the conversation binding (arg index 3) is null for agent-wide memory.
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

    // Resolves any participant set to a fixed conversation id so tests can
    // assert the registry threaded it onto the store call.
    private void StubConversation(Guid conversationId) =>
        _threadRegistry.GetOrCreateAsync(Arg.Any<IEnumerable<Address>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(GuidFormatter.Format(conversationId)));

    [Fact]
    public void GetToolDefinitions_AdvertisesAllEightTools()
    {
        var registry = CreateRegistry();
        registry.GetToolDefinitions().Select(t => t.Name).ShouldBe(new[]
        {
            SvMemorySkillRegistry.MemoryAddTool,
            SvMemorySkillRegistry.MemoryTextAddTool,
            SvMemorySkillRegistry.MemoryGetTool,
            SvMemorySkillRegistry.MemoryListTool,
            SvMemorySkillRegistry.MemorySearchTool,
            SvMemorySkillRegistry.MemoryUpdateTool,
            SvMemorySkillRegistry.MemoryTextUpdateTool,
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
    public async Task MemoryAdd_PassesCallerAddressAsOwner_AndDefaultsToAgentWide()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var ctx = AgentContext(callerId);
        var args = JsonDocument.Parse("""{"content":{"note":"a note"}}""").RootElement;
        StubAddEcho();

        await registry.InvokeAsync(SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken);

        // No participants → agent-wide → no conversation binding.
        await _memoryStore.Received(1).AddAsync(
            Arg.Is<Address>(a => a.Scheme == Address.AgentScheme && a.Id == callerId),
            Arg.Is<JsonElement>(c => c.ValueKind == JsonValueKind.Object),
            null,
            null,
            Arg.Any<CancellationToken>());
        // Agent-wide memory never touches the conversation registry.
        await _threadRegistry.DidNotReceive().GetOrCreateAsync(
            Arg.Any<IEnumerable<Address>>(), Arg.Any<CancellationToken>());
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
    public async Task MemoryAdd_StringContent_RejectsAndPointsToTextVariant()
    {
        // #3038: the object-primary add variant rejects a string so encoding
        // cannot drift back to the old object-vs-stringified union.
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse("""{"content":"plain text"}""").RootElement;

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("sv.memory.text.add");

        await _memoryStore.DidNotReceive().AddAsync(
            Arg.Any<Address>(), Arg.Any<JsonElement>(), Arg.Any<string?>(),
            Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryTextAdd_StringContent_PassesToStore_AndEmitsString()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse("""{"content":"plain note"}""").RootElement;
        StubAddEcho();

        var result = await registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryTextAddTool, args, ctx, TestContext.Current.CancellationToken);

        await _memoryStore.Received(1).AddAsync(
            Arg.Any<Address>(),
            Arg.Is<JsonElement>(c => c.ValueKind == JsonValueKind.String && c.GetString() == "plain note"),
            null,
            null,
            Arg.Any<CancellationToken>());

        var content = result.GetProperty("content");
        content.ValueKind.ShouldBe(JsonValueKind.String);
        content.GetString().ShouldBe("plain note");
    }

    [Fact]
    public async Task MemoryTextAdd_ObjectContent_RejectsAndPointsToStructuredVariant()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse("""{"content":{"k":"v"}}""").RootElement;

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(SvMemorySkillRegistry.MemoryTextAddTool, args, ctx, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("sv.memory.add");
    }

    [Fact]
    public async Task MemoryAdd_WithParticipants_BindsToResolvedConversation_AndAutoIncludesCaller()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var ctx = AgentContext(callerId);
        var other = new Address(Address.HumanScheme, otherId);
        var args = JsonDocument.Parse(
            $$"""{"content":{"x":1},"participants":["{{other}}"]}""").RootElement;
        StubConversation(conversationId);
        StubAddEcho();

        await registry.InvokeAsync(SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken);

        // The participant set is {caller} ∪ {supplied} — the caller is
        // auto-included so the binding matches sv.memory.history_with.
        await _threadRegistry.Received(1).GetOrCreateAsync(
            Arg.Is<IEnumerable<Address>>(set =>
                set.Any(a => a.Id == callerId) && set.Any(a => a.Id == otherId)),
            Arg.Any<CancellationToken>());

        // The resolved conversation id is the store binding.
        await _memoryStore.Received(1).AddAsync(
            Arg.Is<Address>(a => a.Id == callerId),
            Arg.Any<JsonElement>(),
            null,
            conversationId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryAdd_NoParticipants_DoesNotUseTheActiveConversation()
    {
        // #3041 Part A: the active conversation no longer implicitly scopes
        // memory. Without participants an add is agent-wide even when the
        // caller is serving a conversation.
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid(), Guid.NewGuid());
        var args = JsonDocument.Parse("""{"content":{"k":"v"}}""").RootElement;
        StubAddEcho();

        await registry.InvokeAsync(SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken);

        await _memoryStore.Received(1).AddAsync(
            Arg.Any<Address>(), Arg.Any<JsonElement>(), null, null, Arg.Any<CancellationToken>());
        await _threadRegistry.DidNotReceive().GetOrCreateAsync(
            Arg.Any<IEnumerable<Address>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryList_NoParticipants_RecallsAgentWide()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var ctx = AgentContext(callerId, Guid.NewGuid());
        var args = JsonDocument.Parse("""{"limit":20,"offset":5}""").RootElement;
        _memoryStore.ListAsync(Arg.Any<Address>(), Arg.Any<MemoryScope?>(), Arg.Any<Guid?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>()));

        await registry.InvokeAsync(SvMemorySkillRegistry.MemoryListTool, args, ctx, TestContext.Current.CancellationToken);

        // Agent-wide bucket: scope=Agent (thread_id IS NULL), no binding.
        await _memoryStore.Received(1).ListAsync(
            Arg.Is<Address>(a => a.Id == callerId),
            MemoryScope.Agent,
            null,
            20,
            5,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryList_WithParticipants_RecallsThatConversationOnly()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var other = new Address(Address.AgentScheme, Guid.NewGuid());
        var ctx = AgentContext(callerId);
        var args = JsonDocument.Parse($$"""{"participants":["{{other}}"]}""").RootElement;
        StubConversation(conversationId);
        _memoryStore.ListAsync(Arg.Any<Address>(), Arg.Any<MemoryScope?>(), Arg.Any<Guid?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>()));

        await registry.InvokeAsync(SvMemorySkillRegistry.MemoryListTool, args, ctx, TestContext.Current.CancellationToken);

        // Conversation bucket: scope=Thread + the resolved binding narrows
        // the store predicate to exactly that conversation's entries.
        await _memoryStore.Received(1).ListAsync(
            Arg.Is<Address>(a => a.Id == callerId),
            MemoryScope.Thread,
            conversationId,
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemorySearch_NoParticipants_RecallsAgentWide()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var ctx = AgentContext(callerId, Guid.NewGuid());
        var args = JsonDocument.Parse("""{"query":"react hooks","limit":5}""").RootElement;
        _memoryStore.SearchAsync(Arg.Any<Address>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>()));

        await registry.InvokeAsync(SvMemorySkillRegistry.MemorySearchTool, args, ctx, TestContext.Current.CancellationToken);

        await _memoryStore.Received(1).SearchAsync(
            Arg.Is<Address>(a => a.Id == callerId),
            "react hooks",
            MemoryScope.Agent,
            null,
            5,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemorySearch_WithParticipants_RecallsThatConversationOnly()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var other = new Address(Address.AgentScheme, Guid.NewGuid());
        var ctx = AgentContext(callerId);
        var args = JsonDocument.Parse($$"""{"query":"bob","participants":["{{other}}"]}""").RootElement;
        StubConversation(conversationId);
        _memoryStore.SearchAsync(Arg.Any<Address>(), Arg.Any<string>(), Arg.Any<MemoryScope?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>()));

        await registry.InvokeAsync(SvMemorySkillRegistry.MemorySearchTool, args, ctx, TestContext.Current.CancellationToken);

        await _memoryStore.Received(1).SearchAsync(
            Arg.Is<Address>(a => a.Id == callerId),
            "bob",
            MemoryScope.Thread,
            conversationId,
            Arg.Any<int>(),
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
    public async Task MemoryUpdate_StringContent_RejectsAndPointsToTextVariant()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var id = Guid.NewGuid();
        var args = JsonDocument.Parse($$"""{"id":"{{id.ToString("N")}}","content":"archived"}""").RootElement;

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(SvMemorySkillRegistry.MemoryUpdateTool, args, ctx, TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("sv.memory.text.update");
    }

    [Fact]
    public async Task MemoryTextUpdate_StringContent_PassesStringToStore()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var ctx = AgentContext(callerId);
        var args = JsonDocument.Parse($$"""{"id":"{{id.ToString("N")}}","content":"archived"}""").RootElement;
        _memoryStore.UpdateAsync(Arg.Any<Address>(), Arg.Any<Guid>(),
                Arg.Any<JsonElement?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult<MemoryEntry?>(new MemoryEntry(
                id, call.Arg<Address>(),
                call.ArgAt<JsonElement?>(2)!.Value, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

        var result = await registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryTextUpdateTool, args, ctx, TestContext.Current.CancellationToken);

        await _memoryStore.Received(1).UpdateAsync(
            Arg.Is<Address>(a => a.Id == callerId),
            id,
            Arg.Is<JsonElement?>(c => c.HasValue && c.Value.ValueKind == JsonValueKind.String),
            Arg.Any<CancellationToken>());
        result.GetProperty("content").GetString().ShouldBe("archived");
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
    public async Task MemoryEntry_Result_OmitsScopeAndConversationBinding()
    {
        // #3041 Part A: the wire shape no longer surfaces scope / thread_id —
        // the caller knows the bucket from how it queried.
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse("""{"content":{"k":"v"}}""").RootElement;
        StubAddEcho();

        var result = await registry.InvokeAsync(
            SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken);

        result.TryGetProperty("scope", out _).ShouldBeFalse();
        result.TryGetProperty("thread_id", out _).ShouldBeFalse();
        result.TryGetProperty("id", out _).ShouldBeTrue();
        result.TryGetProperty("content", out _).ShouldBeTrue();
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
        var args = JsonDocument.Parse($$$"""{"id":"{{{id.ToString("N")}}}","content":{"k":"v"}}""").RootElement;
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
        var args = JsonDocument.Parse("""{"content":{"k":"v"}}""").RootElement;

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
    public async Task MemoryTextAdd_EmptyStringContent_ThrowsArgumentException()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse("""{"content":"   "}""").RootElement;

        await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(SvMemorySkillRegistry.MemoryTextAddTool, args, ctx, TestContext.Current.CancellationToken));
    }
}
