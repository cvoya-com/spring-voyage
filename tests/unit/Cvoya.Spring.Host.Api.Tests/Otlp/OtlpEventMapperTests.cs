// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Otlp;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Endpoints.Otlp;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Issue #2492 — verifies OTLP → ActivityEvent mapping, including the
/// resource-attribute / principal cross-check that prevents a leaked
/// callback token from replaying against another tenant.
/// </summary>
public class OtlpEventMapperTests
{
    private static readonly Guid TenantA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid SubjectA = new("11111111-0000-0000-0000-000000000001");

    [Fact]
    public void MapTraces_MatchingResource_EmitsOneEventPerSpan()
    {
        var request = new OtlpTracesRequest
        {
            ResourceSpans =
            {
                new OtlpResourceSpans
                {
                    Resource = ResourceFor(TenantA, SubjectA, "agent"),
                    ScopeSpans =
                    {
                        new OtlpScopeSpans
                        {
                            Spans =
                            {
                                Span("sv.agent.invoke", startUnixNanos: "1000000000000000000"),
                                Span("sv.llm.turn", startUnixNanos: "1000000000000000001"),
                            },
                        },
                    },
                },
            },
        };

        var events = OtlpEventMapper.MapTraces(request, TenantA,
            new Address(Address.AgentScheme, SubjectA), NullLogger.Instance);

        events.Count.ShouldBe(2);
        events[0].Kind.ShouldBe(OtlpEventKind.Span);
        events[1].Kind.ShouldBe(OtlpEventKind.LlmTurn);
    }

    [Fact]
    public void MapTraces_MismatchedTenant_DropsBatch()
    {
        var request = new OtlpTracesRequest
        {
            ResourceSpans =
            {
                new OtlpResourceSpans
                {
                    // Resource declares tenant B but the authenticated
                    // principal is tenant A — the cross-check must drop the batch.
                    Resource = ResourceFor(TenantB, SubjectA, "agent"),
                    ScopeSpans =
                    {
                        new OtlpScopeSpans { Spans = { Span("sv.agent.invoke") } },
                    },
                },
            },
        };

        var events = OtlpEventMapper.MapTraces(request, TenantA,
            new Address(Address.AgentScheme, SubjectA), NullLogger.Instance);

        events.ShouldBeEmpty();
    }

    [Fact]
    public void MapTraces_SpanWithProgressEvent_EmitsAdditionalProgressEvent()
    {
        var span = Span("sv.agent.invoke");
        span.Events.Add(new OtlpSpanEvent
        {
            Name = "sv.progress",
            TimeUnixNano = "1000000000000000002",
            Attributes =
            {
                new OtlpKeyValue { Key = "message", Value = new OtlpAnyValue { StringValue = "halfway done" } },
            },
        });

        var request = new OtlpTracesRequest
        {
            ResourceSpans =
            {
                new OtlpResourceSpans
                {
                    Resource = ResourceFor(TenantA, SubjectA, "agent"),
                    ScopeSpans = { new OtlpScopeSpans { Spans = { span } } },
                },
            },
        };

        var events = OtlpEventMapper.MapTraces(request, TenantA,
            new Address(Address.AgentScheme, SubjectA), NullLogger.Instance);

        events.Count.ShouldBe(2); // The span + the sv.progress event
        events.ShouldContain(e => e.Kind == OtlpEventKind.Progress && e.Summary == "halfway done");
    }

    [Fact]
    public void MapLogs_HumanSubject_StampsHumanScheme()
    {
        var humanId = new Guid("22222222-0000-0000-0000-000000000001");
        var request = new OtlpLogsRequest
        {
            ResourceLogs =
            {
                new OtlpResourceLogs
                {
                    Resource = ResourceFor(TenantA, humanId, "human"),
                    ScopeLogs =
                    {
                        new OtlpScopeLogs
                        {
                            LogRecords = { new OtlpLogRecord { SeverityText = "INFO", Body = new OtlpAnyValue { StringValue = "notification dispatched" } } },
                        },
                    },
                },
            },
        };

        var events = OtlpEventMapper.MapLogs(request, TenantA,
            new Address(Address.HumanScheme, humanId), NullLogger.Instance);

        events.Count.ShouldBe(1);
        events[0].Subject.Scheme.ShouldBe(Address.HumanScheme);
        events[0].Subject.Id.ShouldBe(humanId);
        events[0].Kind.ShouldBe(OtlpEventKind.Log);
    }

    [Fact]
    public void MapLogs_SeverityNumber_MappedCorrectly()
    {
        var request = LogRequest("severity-number=20", severityNumber: 20);

        var events = OtlpEventMapper.MapLogs(request, TenantA,
            new Address(Address.AgentScheme, SubjectA), NullLogger.Instance);

        events.Count.ShouldBe(1);
        events[0].Severity.ShouldBe(ActivitySeverity.Error);
    }

    private static OtlpLogsRequest LogRequest(string body, int? severityNumber = null)
        => new()
        {
            ResourceLogs =
            {
                new OtlpResourceLogs
                {
                    Resource = ResourceFor(TenantA, SubjectA, "agent"),
                    ScopeLogs =
                    {
                        new OtlpScopeLogs
                        {
                            LogRecords =
                            {
                                new OtlpLogRecord
                                {
                                    SeverityNumber = severityNumber,
                                    Body = new OtlpAnyValue { StringValue = body },
                                },
                            },
                        },
                    },
                },
            },
        };

    private static OtlpResource ResourceFor(Guid tenantId, Guid subjectId, string subjectKind)
        => new()
        {
            Attributes =
            {
                new OtlpKeyValue { Key = "sv.tenant.id", Value = new OtlpAnyValue { StringValue = GuidFormatter.Format(tenantId) } },
                new OtlpKeyValue { Key = "sv.subject.uuid", Value = new OtlpAnyValue { StringValue = GuidFormatter.Format(subjectId) } },
                new OtlpKeyValue { Key = "sv.subject.kind", Value = new OtlpAnyValue { StringValue = subjectKind } },
            },
        };

    private static OtlpSpan Span(string name, string? startUnixNanos = null)
        => new()
        {
            Name = name,
            StartTimeUnixNano = startUnixNanos ?? "1000000000000000000",
            EndTimeUnixNano = "1000000000000000999",
        };
}
