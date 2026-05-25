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
/// Contract + dispatch tests for <see cref="SvMemoryHistoryRegistry"/>
/// (#2747). Pin: the tool surface (engagements / history_with /
/// search_messages), the no-context overload rejection, the unknown-tool
/// rejection, sender auto-include in the participant set, the
/// caller-scoping behaviour (a thread the caller is not on is invisible),
/// and the wire shape — including that no <c>thread_id</c> ever surfaces
/// to the agent.
/// </summary>
public class SvMemoryHistoryRegistryTests
{
    private readonly IThreadQueryService _queryService = Substitute.For<IThreadQueryService>();
    private readonly IThreadRegistry _threadRegistry = Substitute.For<IThreadRegistry>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    public SvMemoryHistoryRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
    }

    private SvMemoryHistoryRegistry CreateRegistry()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(_loggerFactory);
        services.AddSingleton(_queryService);
        services.AddSingleton(_threadRegistry);
        var sp = services.BuildServiceProvider();
        return new SvMemoryHistoryRegistry(
            scopeFactory: sp.GetRequiredService<IServiceScopeFactory>(),
            loggerFactory: _loggerFactory);
    }

    private static ToolCallContext AgentContext(Guid callerId) =>
        new(
            CallerId: GuidFormatter.Format(callerId),
            CallerKind: Address.AgentScheme,
            ThreadId: GuidFormatter.Format(Guid.NewGuid()));

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Name_IsSv() => CreateRegistry().Name.ShouldBe("sv");

    [Fact]
    public void GetToolDefinitions_AdvertisesAllThreeTools()
    {
        var registry = CreateRegistry();
        registry.GetToolDefinitions().Select(t => t.Name).ShouldBe(new[]
        {
            SvMemoryHistoryRegistry.EngagementsTool,
            SvMemoryHistoryRegistry.HistoryWithTool,
            SvMemoryHistoryRegistry.SearchMessagesTool,
        });
        // All three live under the unified memory category — the participant-
        // set rename in #2747 collapses the old thread/memory split.
        registry.GetToolDefinitions().ShouldAllBe(t => t.Category == ToolCategories.Memory);
    }

    [Fact]
    public async Task NoContextOverload_AlwaysThrows()
    {
        var registry = CreateRegistry();
        await Should.ThrowAsync<SpringException>(async () =>
            await registry.InvokeAsync(
                SvMemoryHistoryRegistry.EngagementsTool,
                Args("{}"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RichInvokeAsync_UnknownTool_ThrowsSkillNotFound()
    {
        var registry = CreateRegistry();
        await Should.ThrowAsync<SkillNotFoundException>(async () =>
            await registry.InvokeAsync(
                "sv.memory.does_not_exist", Args("{}"),
                AgentContext(Guid.NewGuid()),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Engagements_PassesCallerAddressAsParticipantFilter()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var caller = new Address(Address.AgentScheme, callerId);
        var summary = BuildSummary(
            GuidFormatter.Format(Guid.NewGuid()),
            caller,
            new Address(Address.HumanScheme, Guid.NewGuid()));

        _queryService.ListAsync(Arg.Any<ThreadQueryFilters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ThreadSummary>>(new[] { summary }));

        var json = await registry.InvokeAsync(
            SvMemoryHistoryRegistry.EngagementsTool, Args("""{"limit":20}"""),
            AgentContext(callerId), TestContext.Current.CancellationToken);

        await _queryService.Received(1).ListAsync(
            Arg.Is<ThreadQueryFilters>(f =>
                f.Participant == caller.ToString() && f.Limit == 20),
            Arg.Any<CancellationToken>());

        json.GetProperty("engagements").GetArrayLength().ShouldBe(1);
        var engagement = json.GetProperty("engagements")[0];
        engagement.GetProperty("participants").GetArrayLength().ShouldBe(2);
        // #2747 — the engagement wire shape never exposes a thread_id.
        engagement.TryGetProperty("thread_id", out _).ShouldBeFalse();
        engagement.TryGetProperty("id", out _).ShouldBeFalse();
        json.GetProperty("count").GetInt32().ShouldBe(1);
        json.GetProperty("limit").GetInt32().ShouldBe(20);
    }

    [Fact]
    public async Task HistoryWith_ResolvesThreadFromParticipantSet_AutoIncludingCaller()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var caller = new Address(Address.AgentScheme, callerId);
        var other = new Address(Address.HumanScheme, Guid.NewGuid());
        var resolvedThread = GuidFormatter.Format(Guid.NewGuid());

        _threadRegistry.GetOrCreateAsync(
                Arg.Is<IEnumerable<Address>>(p =>
                    p.Any(a => a.ToString() == caller.ToString())
                    && p.Any(a => a.ToString() == other.ToString())),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(resolvedThread));

        var summary = BuildSummary(resolvedThread, caller, other);
        _queryService.GetAsync(resolvedThread, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ThreadDetail?>(new ThreadDetail(summary, Array.Empty<ThreadEvent>())));

        var json = await registry.InvokeAsync(
            SvMemoryHistoryRegistry.HistoryWithTool,
            Args($$$"""{"participants": ["{{{other}}}"]}"""),
            AgentContext(callerId), TestContext.Current.CancellationToken);

        json.GetProperty("participants").GetArrayLength().ShouldBe(2);
        // The history wire shape carries participants + messages, never a thread_id.
        json.TryGetProperty("thread_id", out _).ShouldBeFalse();
        json.TryGetProperty("id", out _).ShouldBeFalse();
        json.GetProperty("messages").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task HistoryWith_ReturnsNull_WhenCallerNotParticipant()
    {
        // Caller scoping: the registry sets the participant set to {caller} ∪
        // supplied. If the resolved thread's participants don't include the
        // caller (corrupt registry, racy state), the call returns null.
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var caller = new Address(Address.AgentScheme, callerId);
        var other = new Address(Address.HumanScheme, Guid.NewGuid());
        var otherOther = new Address(Address.HumanScheme, Guid.NewGuid());
        var resolvedThread = GuidFormatter.Format(Guid.NewGuid());

        _threadRegistry.GetOrCreateAsync(
                Arg.Any<IEnumerable<Address>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(resolvedThread));

        var summary = BuildSummary(resolvedThread, other, otherOther);
        _queryService.GetAsync(resolvedThread, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ThreadDetail?>(new ThreadDetail(summary, Array.Empty<ThreadEvent>())));

        var json = await registry.InvokeAsync(
            SvMemoryHistoryRegistry.HistoryWithTool,
            Args($$$"""{"participants": ["{{{other}}}"]}"""),
            AgentContext(callerId), TestContext.Current.CancellationToken);

        json.ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task HistoryWith_DedupesCaller_WhenExplicitlyListed()
    {
        // #2747 — participants is a set; the caller is auto-included, so
        // explicitly listing them is a no-op (does not double-count when
        // resolving the thread). The participant set passed to
        // IThreadRegistry must contain the caller exactly once.
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var caller = new Address(Address.AgentScheme, callerId);
        var other = new Address(Address.HumanScheme, Guid.NewGuid());
        var resolvedThread = GuidFormatter.Format(Guid.NewGuid());

        IEnumerable<Address>? captured = null;
        _threadRegistry.GetOrCreateAsync(
                Arg.Do<IEnumerable<Address>>(p => captured = p),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(resolvedThread));

        _queryService.GetAsync(resolvedThread, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ThreadDetail?>(new ThreadDetail(
                new ThreadSummary(
                    Id: resolvedThread,
                    Participants: new[] { caller.ToString(), other.ToString() },
                    LastActivity: DateTimeOffset.UtcNow,
                    CreatedAt: DateTimeOffset.UtcNow,
                    EventCount: 0,
                    Origin: caller.ToString(),
                    Summary: string.Empty),
                Array.Empty<ThreadEvent>())));

        await registry.InvokeAsync(
            SvMemoryHistoryRegistry.HistoryWithTool,
            Args($$$"""{"participants": ["{{{caller}}}", "{{{other}}}"]}"""),
            AgentContext(callerId), TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        var keys = captured!.Select(a => a.ToString()).ToList();
        keys.Count(k => k == caller.ToString()).ShouldBe(1);
        keys.ShouldContain(other.ToString());
    }

    [Fact]
    public async Task HistoryWith_RequiresParticipants()
    {
        var registry = CreateRegistry();
        await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(
                SvMemoryHistoryRegistry.HistoryWithTool,
                Args("""{}"""),
                AgentContext(Guid.NewGuid()),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SearchMessages_NoParticipants_SearchesAllCallerThreads()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var caller = new Address(Address.AgentScheme, callerId);

        _queryService.SearchAsync(
                caller.ToString(),
                "needle",
                threadId: null,
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ThreadSearchHit>>(Array.Empty<ThreadSearchHit>()));

        var json = await registry.InvokeAsync(
            SvMemoryHistoryRegistry.SearchMessagesTool,
            Args("""{"query":"needle"}"""),
            AgentContext(callerId), TestContext.Current.CancellationToken);

        await _queryService.Received(1).SearchAsync(
            caller.ToString(), "needle", null, Arg.Any<int>(), Arg.Any<CancellationToken>());
        json.GetProperty("hits").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task SearchMessages_WithParticipants_ScopesToResolvedThread()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var caller = new Address(Address.AgentScheme, callerId);
        var other = new Address(Address.HumanScheme, Guid.NewGuid());
        var resolvedThread = GuidFormatter.Format(Guid.NewGuid());

        _threadRegistry.GetOrCreateAsync(
                Arg.Is<IEnumerable<Address>>(p =>
                    p.Any(a => a.ToString() == caller.ToString())
                    && p.Any(a => a.ToString() == other.ToString())),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(resolvedThread));

        _queryService.SearchAsync(
                caller.ToString(), "needle", resolvedThread, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ThreadSearchHit>>(Array.Empty<ThreadSearchHit>()));

        await registry.InvokeAsync(
            SvMemoryHistoryRegistry.SearchMessagesTool,
            Args($$$"""{"query":"needle","participants": ["{{{other}}}"]}"""),
            AgentContext(callerId), TestContext.Current.CancellationToken);

        await _queryService.Received(1).SearchAsync(
            caller.ToString(), "needle", resolvedThread, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private static ThreadSummary BuildSummary(string threadId, params Address[] participants) =>
        new(
            Id: threadId,
            Participants: participants.Select(a => a.ToString()).ToList(),
            LastActivity: DateTimeOffset.UtcNow,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-10),
            EventCount: 0,
            Origin: participants.FirstOrDefault()?.ToString() ?? string.Empty,
            Summary: "summary");
}
