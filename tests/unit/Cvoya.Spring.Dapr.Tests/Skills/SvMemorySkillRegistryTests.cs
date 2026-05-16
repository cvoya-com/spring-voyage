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
/// load-bearing tools (memory_add, topic_add).
/// </summary>
public class SvMemorySkillRegistryTests
{
    private readonly IMemoryStore _memoryStore = Substitute.For<IMemoryStore>();
    private readonly IMemoryTopicStore _topicStore = Substitute.For<IMemoryTopicStore>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    public SvMemorySkillRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
    }

    private SvMemorySkillRegistry CreateRegistry() => new(_memoryStore, _topicStore, _loggerFactory);

    private static ToolCallContext AgentContext(Guid callerId, Guid? threadId = null) =>
        new(
            CallerId: GuidFormatter.Format(callerId),
            CallerKind: Address.AgentScheme,
            ThreadId: threadId is { } t ? GuidFormatter.Format(t) : Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetToolDefinitions_AdvertisesAllTwelveTools()
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
            SvMemorySkillRegistry.TopicAddTool,
            SvMemorySkillRegistry.TopicGetTool,
            SvMemorySkillRegistry.TopicListTool,
            SvMemorySkillRegistry.TopicSearchTool,
            SvMemorySkillRegistry.TopicUpdateTool,
            SvMemorySkillRegistry.TopicDeleteTool,
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
    public async Task MemoryAdd_PassesCallerAddressAsOwner_AndDefaultsKindToLongTerm()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var ctx = AgentContext(callerId);
        var args = JsonDocument.Parse("""{"content":"a note"}""").RootElement;

        _memoryStore.AddAsync(Arg.Any<Address>(), Arg.Any<MemoryKind>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new MemoryEntry(
                Guid.NewGuid(),
                call.Arg<Address>(),
                call.Arg<MemoryKind>(),
                call.ArgAt<string>(2),
                call.ArgAt<string?>(3),
                call.ArgAt<Guid?>(4),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                call.ArgAt<IReadOnlyList<Guid>>(5))));

        await registry.InvokeAsync(SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken);

        await _memoryStore.Received(1).AddAsync(
            Arg.Is<Address>(a => a.Scheme == Address.AgentScheme && a.Id == callerId),
            MemoryKind.LongTerm,
            "a note",
            null,
            null,
            Arg.Is<IReadOnlyList<Guid>>(l => l.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryAdd_ShortTermDefaultsToCallerThreadId()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var ctx = AgentContext(callerId, threadId);
        var args = JsonDocument.Parse("""{"content":"working","kind":"short_term"}""").RootElement;

        _memoryStore.AddAsync(Arg.Any<Address>(), Arg.Any<MemoryKind>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new MemoryEntry(
                Guid.NewGuid(), call.Arg<Address>(), call.Arg<MemoryKind>(),
                call.ArgAt<string>(2), call.ArgAt<string?>(3), call.ArgAt<Guid?>(4),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, call.ArgAt<IReadOnlyList<Guid>>(5))));

        await registry.InvokeAsync(SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken);

        await _memoryStore.Received(1).AddAsync(
            Arg.Any<Address>(),
            MemoryKind.ShortTerm,
            "working",
            null,
            threadId,
            Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryAdd_ShortTermExplicitThreadId_OverridesDefault()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var override_ = Guid.NewGuid();
        var ctx = AgentContext(callerId, threadId);
        var args = JsonDocument.Parse(
            $$"""{"content":"x","kind":"short_term","thread_id":"{{override_.ToString("N")}}"}""")
            .RootElement;

        _memoryStore.AddAsync(Arg.Any<Address>(), Arg.Any<MemoryKind>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<IReadOnlyList<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new MemoryEntry(
                Guid.NewGuid(), call.Arg<Address>(), call.Arg<MemoryKind>(),
                call.ArgAt<string>(2), call.ArgAt<string?>(3), call.ArgAt<Guid?>(4),
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, call.ArgAt<IReadOnlyList<Guid>>(5))));

        await registry.InvokeAsync(SvMemorySkillRegistry.MemoryAddTool, args, ctx, TestContext.Current.CancellationToken);

        await _memoryStore.Received(1).AddAsync(
            Arg.Any<Address>(),
            MemoryKind.ShortTerm,
            Arg.Any<string>(),
            Arg.Any<string?>(),
            override_,
            Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryList_PassesPagingAndFilters()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var topicId = Guid.NewGuid();
        var ctx = AgentContext(callerId);
        var args = JsonDocument.Parse(
            $$"""{"kind":"short_term","topic_id":"{{topicId.ToString("N")}}","limit":20,"offset":5}""")
            .RootElement;
        _memoryStore.ListAsync(Arg.Any<Address>(), Arg.Any<MemoryKind?>(), Arg.Any<Guid?>(),
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>()));

        await registry.InvokeAsync(SvMemorySkillRegistry.MemoryListTool, args, ctx, TestContext.Current.CancellationToken);

        await _memoryStore.Received(1).ListAsync(
            Arg.Is<Address>(a => a.Id == callerId),
            MemoryKind.ShortTerm,
            topicId,
            20,
            5,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemorySearch_PassesQueryAndCallerOwner()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var ctx = AgentContext(callerId);
        var args = JsonDocument.Parse("""{"query":"react hooks","limit":5}""").RootElement;
        _memoryStore.SearchAsync(Arg.Any<Address>(), Arg.Any<string>(), Arg.Any<MemoryKind?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<MemoryEntry>>(Array.Empty<MemoryEntry>()));

        await registry.InvokeAsync(SvMemorySkillRegistry.MemorySearchTool, args, ctx, TestContext.Current.CancellationToken);

        await _memoryStore.Received(1).SearchAsync(
            Arg.Is<Address>(a => a.Id == callerId),
            "react hooks",
            null,
            null,
            5,
            Arg.Any<CancellationToken>());
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
    public async Task MemoryUpdate_MissingEntry_Throws()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var id = Guid.NewGuid();
        var args = JsonDocument.Parse($$"""{"id":"{{id.ToString("N")}}","content":"x"}""").RootElement;
        _memoryStore.UpdateAsync(Arg.Any<Address>(), Arg.Any<Guid>(),
                Arg.Any<string?>(), Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryEntry?>(null));

        await Should.ThrowAsync<SpringException>(async () =>
            await registry.InvokeAsync(SvMemorySkillRegistry.MemoryUpdateTool, args, ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TopicAdd_PassesCallerOwner()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var ctx = AgentContext(callerId);
        var args = JsonDocument.Parse("""{"name":"design","description":"why"}""").RootElement;
        _topicStore.AddAsync(Arg.Any<Address>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new MemoryTopic(
                Guid.NewGuid(),
                call.Arg<Address>(),
                call.ArgAt<string>(1),
                call.ArgAt<string?>(2),
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)));

        await registry.InvokeAsync(SvMemorySkillRegistry.TopicAddTool, args, ctx, TestContext.Current.CancellationToken);

        await _topicStore.Received(1).AddAsync(
            Arg.Is<Address>(a => a.Id == callerId),
            "design",
            "why",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TopicGet_NotFound_ReturnsJsonNull()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var id = Guid.NewGuid();
        var args = JsonDocument.Parse($$"""{"id":"{{id.ToString("N")}}"}""").RootElement;
        _topicStore.GetAsync(Arg.Any<Address>(), id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<MemoryTopic?>(null));

        var result = await registry.InvokeAsync(SvMemorySkillRegistry.TopicGetTool, args, ctx, TestContext.Current.CancellationToken);
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
}
