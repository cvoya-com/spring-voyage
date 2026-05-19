// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
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
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly Guid _tenantId = Guid.NewGuid();

    public SvRuntimeSkillRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _tenant.CurrentTenantId.Returns(_tenantId);
        _ingest.IngestAsync(Arg.Any<IReadOnlyList<OtlpEventIngest>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OtlpIngestResult(0, 0, 0, 0)));
    }

    private SvRuntimeSkillRegistry CreateRegistry() => new(_ingest, _tenant, _loggerFactory);

    private static ToolCallContext AgentContext(Guid callerId) =>
        new(
            CallerId: GuidFormatter.Format(callerId),
            CallerKind: Address.AgentScheme,
            ThreadId: Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetToolDefinitions_AdvertisesReportProgress()
    {
        var registry = CreateRegistry();
        registry.GetToolDefinitions().Select(t => t.Name)
            .ShouldBe(new[] { SvRuntimeSkillRegistry.ReportProgressTool });
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
}
