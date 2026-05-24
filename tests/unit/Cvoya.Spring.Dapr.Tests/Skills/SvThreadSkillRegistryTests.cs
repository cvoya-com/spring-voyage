// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Contract + dispatch tests for <see cref="SvThreadSkillRegistry"/>
/// (#2683). Pin: the tool surface, the no-context overload rejection,
/// the unknown-tool rejection, the caller-scoping behaviour (the caller
/// is always passed through as the participant filter and threads the
/// caller is not on are hidden), and the wire shape each tool returns.
/// </summary>
public class SvThreadSkillRegistryTests
{
    private readonly IThreadQueryService _queryService = Substitute.For<IThreadQueryService>();
    private readonly IThreadRegistry _threadRegistry = Substitute.For<IThreadRegistry>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    public SvThreadSkillRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
    }

    private SvThreadSkillRegistry CreateRegistry(out IServiceProvider serviceProvider)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(_loggerFactory);
        services.AddSingleton(_queryService);
        services.AddSingleton(_threadRegistry);
        serviceProvider = services.BuildServiceProvider();
        return new SvThreadSkillRegistry(
            scopeFactory: serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            loggerFactory: _loggerFactory);
    }

    private static ToolCallContext AgentContext(Guid callerId, Guid? threadId = null) =>
        new(
            CallerId: GuidFormatter.Format(callerId),
            CallerKind: Address.AgentScheme,
            ThreadId: threadId is { } t ? GuidFormatter.Format(t) : Guid.NewGuid().ToString("N"));

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static ThreadSummary BuildSummary(string threadId, Address caller, params Address[] others)
    {
        var participants = new List<string> { caller.ToString() };
        participants.AddRange(others.Select(a => a.ToString()));
        return new ThreadSummary(
            Id: threadId,
            Participants: participants,
            LastActivity: DateTimeOffset.UtcNow,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            EventCount: 3,
            Origin: others.FirstOrDefault()?.ToString() ?? caller.ToString(),
            Summary: "first message body");
    }

    [Fact]
    public void Name_IsSv()
    {
        CreateRegistry(out _).Name.ShouldBe("sv");
    }

    [Fact]
    public void GetToolDefinitions_AdvertisesAllFourTools()
    {
        var registry = CreateRegistry(out _);
        registry.GetToolDefinitions().Select(t => t.Name).ShouldBe(new[]
        {
            SvThreadSkillRegistry.ThreadListTool,
            SvThreadSkillRegistry.ThreadGetTool,
            SvThreadSkillRegistry.ThreadSearchTool,
            SvThreadSkillRegistry.ThreadParticipantsTool,
        });
        registry.GetToolDefinitions().ShouldAllBe(t => t.Category == ToolCategories.Thread);
    }

    [Fact]
    public async Task NoContextOverload_AlwaysThrows()
    {
        var registry = CreateRegistry(out _);
        await Should.ThrowAsync<SpringException>(async () =>
            await registry.InvokeAsync(
                SvThreadSkillRegistry.ThreadListTool, Args("{}"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RichInvokeAsync_UnknownTool_ThrowsSkillNotFound()
    {
        var registry = CreateRegistry(out _);
        await Should.ThrowAsync<SkillNotFoundException>(async () =>
            await registry.InvokeAsync(
                "sv.thread.does_not_exist", Args("{}"),
                AgentContext(Guid.NewGuid()),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ThreadList_PassesCallerAddressAsParticipantFilter()
    {
        var registry = CreateRegistry(out _);
        var callerId = Guid.NewGuid();
        var caller = new Address(Address.AgentScheme, callerId);
        var summary = BuildSummary("abc123def456", caller, new Address(Address.HumanScheme, Guid.NewGuid()));

        _queryService.ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ThreadSummary>>(new[] { summary }));

        var json = await registry.InvokeAsync(
            SvThreadSkillRegistry.ThreadListTool, Args("""{"limit":20}"""),
            AgentContext(callerId), TestContext.Current.CancellationToken);

        await _queryService.Received(1).ListAsync(
            Arg.Is<ThreadQueryFilters>(f =>
                f.Participant == caller.ToString()
                && f.Limit == 20),
            Arg.Any<CancellationToken>());

        json.GetProperty("threads").GetArrayLength().ShouldBe(1);
        json.GetProperty("threads")[0].GetProperty("id").GetString().ShouldBe("abc123def456");
        json.GetProperty("threads")[0].GetProperty("participants").GetArrayLength().ShouldBe(2);
        json.GetProperty("count").GetInt32().ShouldBe(1);
        json.GetProperty("limit").GetInt32().ShouldBe(20);
    }

    [Fact]
    public async Task ThreadList_DefaultLimitWhenOmitted()
    {
        var registry = CreateRegistry(out _);
        _queryService.ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ThreadSummary>>(Array.Empty<ThreadSummary>()));

        var json = await registry.InvokeAsync(
            SvThreadSkillRegistry.ThreadListTool, Args("{}"),
            AgentContext(Guid.NewGuid()), TestContext.Current.CancellationToken);

        json.GetProperty("limit").GetInt32().ShouldBe(SvThreadSkillRegistry.DefaultLimit);
    }

    [Fact]
    public async Task ThreadList_ClampsLimitToMaximum()
    {
        var registry = CreateRegistry(out _);
        _queryService.ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ThreadSummary>>(Array.Empty<ThreadSummary>()));

        await registry.InvokeAsync(
            SvThreadSkillRegistry.ThreadListTool, Args("""{"limit":5000}"""),
            AgentContext(Guid.NewGuid()), TestContext.Current.CancellationToken);

        await _queryService.Received(1).ListAsync(
            Arg.Is<ThreadQueryFilters>(f => f.Limit == SvThreadSkillRegistry.MaxLimit),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ThreadGet_NullsResponseWhenThreadNotFound()
    {
        var registry = CreateRegistry(out _);
        _queryService.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ThreadDetail?>(null));

        var threadId = Guid.NewGuid().ToString("N");
        var json = await registry.InvokeAsync(
            SvThreadSkillRegistry.ThreadGetTool, Args($$"""{"thread_id":"{{threadId}}"}"""),
            AgentContext(Guid.NewGuid()), TestContext.Current.CancellationToken);

        json.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task ThreadGet_NullsResponseWhenCallerIsNotParticipant()
    {
        var registry = CreateRegistry(out _);
        var threadId = Guid.NewGuid().ToString("N");
        var otherParticipant = new Address(Address.AgentScheme, Guid.NewGuid());
        var summary = new ThreadSummary(
            Id: threadId,
            Participants: new[] { otherParticipant.ToString() },
            LastActivity: DateTimeOffset.UtcNow,
            CreatedAt: DateTimeOffset.UtcNow,
            EventCount: 0,
            Origin: otherParticipant.ToString(),
            Summary: string.Empty);
        _queryService.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ThreadDetail?>(new ThreadDetail(summary, Array.Empty<ThreadEvent>())));

        var json = await registry.InvokeAsync(
            SvThreadSkillRegistry.ThreadGetTool, Args($$"""{"thread_id":"{{threadId}}"}"""),
            AgentContext(Guid.NewGuid()), TestContext.Current.CancellationToken);

        json.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task ThreadGet_ReturnsSummaryAndMessagesWhenCallerParticipates()
    {
        var registry = CreateRegistry(out _);
        var callerId = Guid.NewGuid();
        var caller = new Address(Address.AgentScheme, callerId);
        var other = new Address(Address.HumanScheme, Guid.NewGuid());
        var threadId = Guid.NewGuid().ToString("N");
        var summary = BuildSummary(threadId, caller, other);
        var events = new[]
        {
            new ThreadEvent(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(-2),
                Source: other.ToString(),
                EventType: "MessageArrived",
                Severity: "Info",
                Summary: "hi",
                MessageId: Guid.NewGuid(),
                From: other.ToString(),
                To: caller.ToString(),
                Body: "hi"),
            new ThreadEvent(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(-1),
                Source: caller.ToString(),
                EventType: "MessageArrived",
                Severity: "Info",
                Summary: "hello back",
                MessageId: Guid.NewGuid(),
                From: caller.ToString(),
                To: other.ToString(),
                Body: "hello back"),
        };
        _queryService.GetAsync(threadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ThreadDetail?>(new ThreadDetail(summary, events)));

        var json = await registry.InvokeAsync(
            SvThreadSkillRegistry.ThreadGetTool, Args($$"""{"thread_id":"{{threadId}}"}"""),
            AgentContext(callerId), TestContext.Current.CancellationToken);

        json.GetProperty("summary").GetProperty("id").GetString().ShouldBe(threadId);
        json.GetProperty("messages").GetArrayLength().ShouldBe(2);
        json.GetProperty("messages")[0].GetProperty("body").GetString().ShouldBe("hi");
        json.GetProperty("messages")[1].GetProperty("body").GetString().ShouldBe("hello back");
    }

    [Fact]
    public async Task ThreadGet_TailReturnsOnlyMostRecentN()
    {
        var registry = CreateRegistry(out _);
        var callerId = Guid.NewGuid();
        var caller = new Address(Address.AgentScheme, callerId);
        var threadId = Guid.NewGuid().ToString("N");
        var summary = BuildSummary(threadId, caller);

        var events = Enumerable.Range(0, 5).Select(i =>
            new ThreadEvent(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(-5 + i),
                Source: caller.ToString(),
                EventType: "MessageArrived",
                Severity: "Info",
                Summary: $"m{i}",
                MessageId: Guid.NewGuid(),
                From: caller.ToString(),
                To: caller.ToString(),
                Body: $"m{i}")).ToArray();

        _queryService.GetAsync(threadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ThreadDetail?>(new ThreadDetail(summary, events)));

        var json = await registry.InvokeAsync(
            SvThreadSkillRegistry.ThreadGetTool,
            Args($$"""{"thread_id":"{{threadId}}","tail":2}"""),
            AgentContext(callerId), TestContext.Current.CancellationToken);

        json.GetProperty("messages").GetArrayLength().ShouldBe(2);
        json.GetProperty("messages")[0].GetProperty("body").GetString().ShouldBe("m3");
        json.GetProperty("messages")[1].GetProperty("body").GetString().ShouldBe("m4");
    }

    [Fact]
    public async Task ThreadSearch_PassesCallerQueryAndOptionalThreadId()
    {
        var registry = CreateRegistry(out _);
        var callerId = Guid.NewGuid();
        var caller = new Address(Address.AgentScheme, callerId);
        var threadId = Guid.NewGuid().ToString("N");

        var hit = new ThreadSearchHit(
            ThreadId: threadId,
            MessageId: Guid.NewGuid(),
            Timestamp: DateTimeOffset.UtcNow,
            From: caller.ToString(),
            To: caller.ToString(),
            Body: "matched body");
        _queryService.SearchAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ThreadSearchHit>>(new[] { hit }));

        var json = await registry.InvokeAsync(
            SvThreadSkillRegistry.ThreadSearchTool,
            Args($$"""{"query":"matched","thread_id":"{{threadId}}","limit":10}"""),
            AgentContext(callerId), TestContext.Current.CancellationToken);

        await _queryService.Received(1).SearchAsync(
            caller.ToString(), "matched", threadId, 10, Arg.Any<CancellationToken>());

        json.GetProperty("hits").GetArrayLength().ShouldBe(1);
        json.GetProperty("hits")[0].GetProperty("thread_id").GetString().ShouldBe(threadId);
        json.GetProperty("hits")[0].GetProperty("body").GetString().ShouldBe("matched body");
        json.GetProperty("count").GetInt32().ShouldBe(1);
        json.GetProperty("limit").GetInt32().ShouldBe(10);
    }

    [Fact]
    public async Task ThreadParticipants_NullsResponseWhenThreadNotFound()
    {
        var registry = CreateRegistry(out _);
        _threadRegistry.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ThreadRegistryEntry?>(null));

        var threadId = Guid.NewGuid().ToString("N");
        var json = await registry.InvokeAsync(
            SvThreadSkillRegistry.ThreadParticipantsTool,
            Args($$"""{"thread_id":"{{threadId}}"}"""),
            AgentContext(Guid.NewGuid()), TestContext.Current.CancellationToken);

        json.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task ThreadParticipants_NullsResponseWhenCallerIsNotParticipant()
    {
        var registry = CreateRegistry(out _);
        var threadId = Guid.NewGuid().ToString("N");
        var other = new Address(Address.AgentScheme, Guid.NewGuid());
        var entry = new ThreadRegistryEntry(
            ThreadId: threadId,
            Participants: new[] { other },
            CreatedAt: DateTimeOffset.UtcNow);
        _threadRegistry.ResolveAsync(threadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ThreadRegistryEntry?>(entry));

        var json = await registry.InvokeAsync(
            SvThreadSkillRegistry.ThreadParticipantsTool,
            Args($$"""{"thread_id":"{{threadId}}"}"""),
            AgentContext(Guid.NewGuid()), TestContext.Current.CancellationToken);

        json.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task ThreadParticipants_ReturnsAddressesWhenCallerParticipates()
    {
        var registry = CreateRegistry(out _);
        var callerId = Guid.NewGuid();
        var caller = new Address(Address.AgentScheme, callerId);
        var other = new Address(Address.HumanScheme, Guid.NewGuid());
        var threadId = Guid.NewGuid().ToString("N");
        var entry = new ThreadRegistryEntry(
            ThreadId: threadId,
            Participants: new[] { caller, other },
            CreatedAt: DateTimeOffset.UtcNow);
        _threadRegistry.ResolveAsync(threadId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ThreadRegistryEntry?>(entry));

        var json = await registry.InvokeAsync(
            SvThreadSkillRegistry.ThreadParticipantsTool,
            Args($$"""{"thread_id":"{{threadId}}"}"""),
            AgentContext(callerId), TestContext.Current.CancellationToken);

        json.GetProperty("thread_id").GetString().ShouldBe(threadId);
        var participants = json.GetProperty("participants").EnumerateArray()
            .Select(p => p.GetString()!)
            .ToList();
        participants.ShouldContain(caller.ToString());
        participants.ShouldContain(other.ToString());
    }

    [Fact]
    public async Task ThreadList_MissingCallerId_Throws()
    {
        var registry = CreateRegistry(out _);
        var ctx = new ToolCallContext(
            CallerId: string.Empty,
            CallerKind: Address.AgentScheme,
            ThreadId: Guid.NewGuid().ToString("N"));

        await Should.ThrowAsync<SpringException>(async () =>
            await registry.InvokeAsync(
                SvThreadSkillRegistry.ThreadListTool, Args("{}"), ctx,
                TestContext.Current.CancellationToken));
    }
}
