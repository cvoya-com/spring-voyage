// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for <see cref="SvRuntimeSkillRegistry"/> (#2493). Pins:
/// the tool surface, the no-context rejection, the caller-scoping
/// behaviour, and the activity-bus emit path.
/// </summary>
public class SvRuntimeSkillRegistryTests
{
    private readonly IOtlpIngestService _ingest = Substitute.For<IOtlpIngestService>();
    private readonly ITenantContext _tenant = Substitute.For<ITenantContext>();
    private readonly IActivityEventBus _activityBus = Substitute.For<IActivityEventBus>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly Guid _tenantId = Guid.NewGuid();

    public SvRuntimeSkillRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _tenant.CurrentTenantId.Returns(_tenantId);
        _ingest.IngestAsync(Arg.Any<IReadOnlyList<OtlpEventIngest>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OtlpIngestResult(0, 0, 0, 0)));
        _activityBus.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
    }

    private SvRuntimeSkillRegistry CreateRegistry() => new(_ingest, _tenant, _activityBus, _loggerFactory);

    private static ToolCallContext AgentContext(Guid callerId) =>
        new(
            CallerId: GuidFormatter.Format(callerId),
            CallerKind: Address.AgentScheme,
            ThreadId: Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetToolDefinitions_AdvertisesReportProgressAndReportDecision()
    {
        var registry = CreateRegistry();
        registry.GetToolDefinitions().Select(t => t.Name)
            .ShouldBe(
                new[]
                {
                    SvRuntimeSkillRegistry.ReportProgressTool,
                    SvRuntimeSkillRegistry.ReportDecisionTool,
                },
                ignoreOrder: true);
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
        var args = JsonDocument.Parse("""{ "text": "hello" }""").RootElement;
        await Should.ThrowAsync<SpringException>(async () =>
            await registry.InvokeAsync(
                SvRuntimeSkillRegistry.ReportProgressTool, args, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RichInvokeAsync_UnknownTool_ThrowsSkillNotFound()
    {
        var registry = CreateRegistry();
        var args = JsonDocument.Parse("{}").RootElement;
        var ctx = AgentContext(Guid.NewGuid());
        await Should.ThrowAsync<SkillNotFoundException>(async () =>
            await registry.InvokeAsync("sv.not_a_tool", args, ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReportProgress_MissingText_Throws()
    {
        var registry = CreateRegistry();
        var args = JsonDocument.Parse("""{}""").RootElement;
        var ctx = AgentContext(Guid.NewGuid());

        await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(
                SvRuntimeSkillRegistry.ReportProgressTool, args, ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReportProgress_PublishesActivityEvent()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var args = JsonDocument.Parse("""{ "text": "starting work on issue #123", "kind": "milestone" }""").RootElement;
        var ctx = AgentContext(callerId);

        var result = await registry.InvokeAsync(
            SvRuntimeSkillRegistry.ReportProgressTool, args, ctx, TestContext.Current.CancellationToken);

        result.GetProperty("ok").GetBoolean().ShouldBeTrue();

        // The ingest service receives a single Progress event for the
        // caller, with the supplied text + kind on the details payload.
        await _ingest.Received(1).IngestAsync(
            Arg.Is<IReadOnlyList<OtlpEventIngest>>(events =>
                events.Count == 1
                && events[0].Kind == OtlpEventKind.Progress
                && events[0].TenantId == _tenantId
                && events[0].Subject.Id == callerId
                && events[0].Summary.Contains("starting work on issue #123")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReportProgress_TransportFailure_DoesNotRaiseToCaller()
    {
        // A broken ingest path must NOT block the agent — best-effort
        // emission. The tool returns ok=true regardless.
        _ingest.IngestAsync(Arg.Any<IReadOnlyList<OtlpEventIngest>>(), Arg.Any<CancellationToken>())
            .Returns<Task<OtlpIngestResult>>(_ => throw new InvalidOperationException("bus down"));

        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse("""{ "text": "hello" }""").RootElement;

        var result = await registry.InvokeAsync(
            SvRuntimeSkillRegistry.ReportProgressTool, args, ctx, TestContext.Current.CancellationToken);

        result.GetProperty("ok").GetBoolean().ShouldBeTrue();
    }

    // ---- #2581: sv.report_decision -------------------------------------

    [Fact]
    public async Task ReportDecision_PublishesDecisionMadeActivity()
    {
        var registry = CreateRegistry();
        var callerId = Guid.NewGuid();
        var threadId = Guid.NewGuid();
        var targetAddress = new Address(Address.AgentScheme, Guid.NewGuid());
        var ctx = new ToolCallContext(
            CallerId: GuidFormatter.Format(callerId),
            CallerKind: Address.UnitScheme,
            ThreadId: threadId.ToString("N"));
        var args = JsonDocument.Parse(
            $$"""
            {
              "kind": "delegate",
              "targets": ["{{targetAddress}}"],
              "rationale": "issue is implementation-ready",
              "outcome": "tool_unavailable",
              "detail": "delegate_to not in tool surface"
            }
            """).RootElement;

        ActivityEvent? captured = null;
        await _activityBus.PublishAsync(
            Arg.Do<ActivityEvent>(e => captured = e), Arg.Any<CancellationToken>());

        var result = await registry.InvokeAsync(
            SvRuntimeSkillRegistry.ReportDecisionTool, args, ctx, TestContext.Current.CancellationToken);

        result.GetProperty("ok").GetBoolean().ShouldBeTrue();
        await _activityBus.Received(1).PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>());

        captured.ShouldNotBeNull();
        captured!.EventType.ShouldBe(ActivityEventType.DecisionMade);
        captured.Severity.ShouldBe(ActivitySeverity.Warning);
        captured.Source.Id.ShouldBe(callerId);
        captured.CorrelationId.ShouldBe(threadId.ToString("D"));

        var decision = JsonSerializer.Deserialize<OrchestrationDecision>(captured.Details!.Value);
        decision.ShouldNotBeNull();
        decision!.Status.ShouldBe(OrchestrationDecisionStatus.NotExecuted);
        decision.Kind.ShouldBe(OrchestrationDecisionKind.Delegate);
        decision.TenantId.ShouldBe(_tenantId);
        decision.ThreadId.ShouldBe(threadId);
        decision.Targets.Single().ToString().ShouldBe(targetAddress.ToString());
        decision.Reason.ShouldBe("issue is implementation-ready");
        decision.Metadata!.Value.GetProperty("executionOutcome").GetString().ShouldBe("tool_unavailable");
        decision.Metadata!.Value.GetProperty("detail").GetString().ShouldBe("delegate_to not in tool surface");
        decision.Metadata!.Value.GetProperty("intendedTargets")[0].GetString()
            .ShouldBe(targetAddress.ToString());
    }

    [Fact]
    public async Task ReportDecision_OutcomeOmitted_RecordsRoutedDecision()
    {
        // #2578: report_decision is generalized — an omitted 'outcome'
        // records a decision that executed (Routed), at Info severity, with
        // no executionOutcome metadata field.
        var registry = CreateRegistry();
        var threadId = Guid.NewGuid();
        var targetAddress = new Address(Address.AgentScheme, Guid.NewGuid());
        var ctx = new ToolCallContext(
            CallerId: GuidFormatter.Format(Guid.NewGuid()),
            CallerKind: Address.UnitScheme,
            ThreadId: threadId.ToString("N"));
        var args = JsonDocument.Parse(
            $$"""
            {
              "kind": "delegate",
              "targets": ["{{targetAddress}}"],
              "rationale": "child owns this work"
            }
            """).RootElement;

        ActivityEvent? captured = null;
        await _activityBus.PublishAsync(
            Arg.Do<ActivityEvent>(e => captured = e), Arg.Any<CancellationToken>());

        var result = await registry.InvokeAsync(
            SvRuntimeSkillRegistry.ReportDecisionTool, args, ctx, TestContext.Current.CancellationToken);

        result.GetProperty("ok").GetBoolean().ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.EventType.ShouldBe(ActivityEventType.DecisionMade);
        captured.Severity.ShouldBe(ActivitySeverity.Info);

        var decision = JsonSerializer.Deserialize<OrchestrationDecision>(captured.Details!.Value);
        decision!.Status.ShouldBe(OrchestrationDecisionStatus.Routed);
        decision.Reason.ShouldBe("child owns this work");
        decision.Metadata!.Value.TryGetProperty("executionOutcome", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ReportDecision_TargetByName_PreservedInMetadata()
    {
        // The runtime may name a target by a human name rather than a
        // canonical address. The verbatim name is preserved in
        // intendedTargets even though it cannot become an Address.
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse(
            """{ "targets": ["ada"], "outcome": "tool_unavailable" }""").RootElement;

        ActivityEvent? captured = null;
        await _activityBus.PublishAsync(
            Arg.Do<ActivityEvent>(e => captured = e), Arg.Any<CancellationToken>());

        await registry.InvokeAsync(
            SvRuntimeSkillRegistry.ReportDecisionTool, args, ctx, TestContext.Current.CancellationToken);

        var decision = JsonSerializer.Deserialize<OrchestrationDecision>(captured!.Details!.Value);
        decision!.Targets.ShouldBeEmpty();
        decision.Metadata!.Value.GetProperty("intendedTargets")[0].GetString().ShouldBe("ada");
    }

    [Fact]
    public async Task ReportDecision_FanoutKind_RecordsEveryTarget()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var first = new Address(Address.AgentScheme, Guid.NewGuid());
        var second = new Address(Address.AgentScheme, Guid.NewGuid());
        var args = JsonDocument.Parse(
            $$"""
            {
              "kind": "fanout",
              "targets": ["{{first}}", "{{second}}"],
              "outcome": "validation_rejected"
            }
            """).RootElement;

        ActivityEvent? captured = null;
        await _activityBus.PublishAsync(
            Arg.Do<ActivityEvent>(e => captured = e), Arg.Any<CancellationToken>());

        await registry.InvokeAsync(
            SvRuntimeSkillRegistry.ReportDecisionTool, args, ctx, TestContext.Current.CancellationToken);

        var decision = JsonSerializer.Deserialize<OrchestrationDecision>(captured!.Details!.Value);
        decision!.Kind.ShouldBe(OrchestrationDecisionKind.Fanout);
        decision.Targets.Length.ShouldBe(2);
        decision.Metadata!.Value.GetProperty("intendedTargets").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task ReportDecision_MissingTargets_Throws()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse("""{ "outcome": "tool_unavailable" }""").RootElement;

        await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(
                SvRuntimeSkillRegistry.ReportDecisionTool, args, ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReportDecision_InvalidOutcome_Throws()
    {
        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse(
            """{ "targets": ["agent://ada"], "outcome": "executed" }""").RootElement;

        await Should.ThrowAsync<ArgumentException>(async () =>
            await registry.InvokeAsync(
                SvRuntimeSkillRegistry.ReportDecisionTool, args, ctx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReportDecision_BusFailure_DoesNotRaiseToCaller()
    {
        _activityBus.PublishAsync(Arg.Any<ActivityEvent>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("bus down"));

        var registry = CreateRegistry();
        var ctx = AgentContext(Guid.NewGuid());
        var args = JsonDocument.Parse(
            """{ "targets": ["agent://ada"], "outcome": "delivery_failed" }""").RootElement;

        var result = await registry.InvokeAsync(
            SvRuntimeSkillRegistry.ReportDecisionTool, args, ctx, TestContext.Current.CancellationToken);

        result.GetProperty("ok").GetBoolean().ShouldBeTrue();
    }
}
