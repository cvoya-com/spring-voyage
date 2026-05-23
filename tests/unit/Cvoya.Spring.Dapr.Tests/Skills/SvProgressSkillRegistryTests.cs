// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for <see cref="SvProgressSkillRegistry"/> (ADR-0056 §8 /
/// #2656). Pins: the tool surface, the no-context rejection, the
/// caller-scoping behaviour, the optional <c>fraction</c> argument,
/// and the activity-bus best-effort emission.
/// </summary>
public class SvProgressSkillRegistryTests
{
    private readonly IActivityEventBus _activityBus = Substitute.For<IActivityEventBus>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly List<ActivityEvent> _published = new();

    public SvProgressSkillRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _activityBus
            .PublishAsync(Arg.Do<ActivityEvent>(evt => _published.Add(evt)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    private SvProgressSkillRegistry CreateRegistry() =>
        new(_activityBus, _loggerFactory);

    private static ToolCallContext AgentContext(Guid callerId, Guid? threadId = null) =>
        new(
            CallerId: GuidFormatter.Format(callerId),
            CallerKind: Address.AgentScheme,
            ThreadId: (threadId ?? Guid.NewGuid()).ToString("N"));

    [Fact]
    public void Name_IsSv()
    {
        CreateRegistry().Name.ShouldBe("sv");
    }

    [Fact]
    public void GetToolDefinitions_AdvertisesReportTool()
    {
        var tools = CreateRegistry().GetToolDefinitions();
        tools.Count.ShouldBe(1);
        tools[0].Name.ShouldBe(SvProgressSkillRegistry.ReportTool);
        tools[0].Category.ShouldBe(ToolCategories.Observability);
    }

    [Fact]
    public async Task NoContextOverload_AlwaysThrows()
    {
        var registry = CreateRegistry();
        var args = JsonDocument.Parse("""{ "message": "x" }""").RootElement;
        await Should.ThrowAsync<SpringException>(async () =>
            await registry.InvokeAsync(
                SvProgressSkillRegistry.ReportTool, args, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RichInvokeAsync_UnknownTool_ThrowsSkillNotFound()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse("{}").RootElement;
        await Should.ThrowAsync<SkillNotFoundException>(async () =>
            await registry.InvokeAsync("sv.not_a_tool", args, ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Report_MissingMessage_Throws()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse("""{}""").RootElement;

        await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(
                SvProgressSkillRegistry.ReportTool, args, ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Report_PublishesRuntimeProgressActivity()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var args = JsonDocument.Parse("""{ "message": "reviewing PR diff" }""").RootElement;

        var result = await registry.InvokeAsync(
            SvProgressSkillRegistry.ReportTool, args,
            AgentContext(callerId, threadId),
            TestContext.Current.CancellationToken);

        result.GetProperty("ok").GetBoolean().ShouldBeTrue();
        _published.Count.ShouldBe(1);
        var evt = _published[0];
        evt.EventType.ShouldBe(ActivityEventType.RuntimeProgress);
        evt.Severity.ShouldBe(ActivitySeverity.Info);
        evt.Summary.ShouldBe("reviewing PR diff");
        evt.Source.Id.ShouldBe(callerId);
        evt.Source.Scheme.ShouldBe(Address.AgentScheme);
        evt.CorrelationId.ShouldBe(threadId.ToString("D"));

        var details = evt.Details!.Value;
        details.GetProperty("message").GetString().ShouldBe("reviewing PR diff");
        details.GetProperty("source").GetString().ShouldBe("mcp:sv.progress.report");
        details.TryGetProperty("fraction", out _).ShouldBeFalse(
            "fraction is omitted from the details payload when not supplied.");
    }

    [Fact]
    public async Task Report_WithFraction_StampsFractionOnDetails()
    {
        var registry = CreateRegistry();
        var args = JsonDocument.Parse("""{ "message": "halfway", "fraction": 0.5 }""").RootElement;

        await registry.InvokeAsync(
            SvProgressSkillRegistry.ReportTool, args,
            AgentContext(Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        _published.Count.ShouldBe(1);
        _published[0].Details!.Value.GetProperty("fraction").GetDouble().ShouldBe(0.5);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public async Task Report_FractionOutOfRange_Throws(double fraction)
    {
        var registry = CreateRegistry();
        var args = JsonDocument
            .Parse($$"""{ "message": "x", "fraction": {{fraction}} }""")
            .RootElement;
        var ctx = AgentContext(Guid.NewGuid());

        await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(
                SvProgressSkillRegistry.ReportTool, args, ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Report_FractionAtBoundary_IsAccepted()
    {
        var registry = CreateRegistry();
        foreach (var fraction in new[] { 0.0, 1.0 })
        {
            var args = JsonDocument
                .Parse($$"""{ "message": "x", "fraction": {{fraction}} }""")
                .RootElement;
            await registry.InvokeAsync(
                SvProgressSkillRegistry.ReportTool, args,
                AgentContext(Guid.NewGuid()),
                TestContext.Current.CancellationToken);
        }
        _published.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Report_BusFailure_DoesNotRaiseToCaller()
    {
        _activityBus
            .PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("bus down"));

        var registry = CreateRegistry();
        var args = JsonDocument.Parse("""{ "message": "hello" }""").RootElement;

        var result = await registry.InvokeAsync(
            SvProgressSkillRegistry.ReportTool, args,
            AgentContext(Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        result.GetProperty("ok").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Report_LongMessage_IsTruncated()
    {
        var registry = CreateRegistry();
        var longMessage = new string('a', SvProgressSkillRegistry.MaxMessageLength + 100);
        var args = JsonDocument
            .Parse(JsonSerializer.Serialize(new { message = longMessage }))
            .RootElement;

        await registry.InvokeAsync(
            SvProgressSkillRegistry.ReportTool, args,
            AgentContext(Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        _published.Count.ShouldBe(1);
        _published[0].Summary.Length.ShouldBe(SvProgressSkillRegistry.MaxMessageLength + 1);
        _published[0].Summary.EndsWith("…").ShouldBeTrue();
    }
}
