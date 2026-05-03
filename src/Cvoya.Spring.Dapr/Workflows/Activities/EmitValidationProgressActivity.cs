// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows.Activities;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

using global::Dapr.Workflow;

using Microsoft.Extensions.Logging;

/// <summary>
/// Publishes a <see cref="ActivityEventType.ValidationProgress"/> activity
/// event on behalf of the <see cref="Cvoya.Spring.Dapr.Workflows.UnitValidationWorkflow"/>.
/// </summary>
/// <remarks>
/// <para>
/// The workflow body stays deterministic and service-free; every progress
/// event flows through this activity so the <see cref="IActivityEventBus"/>
/// dependency lives where DI is actually available.
/// </para>
/// <para>
/// Event source is <c>Address(scheme: "unit", path: UnitName)</c> — the T-06
/// unit detail page filters the activity stream on this exact shape (keyed
/// on the unit's user-facing name, not its actor Guid). The payload carries
/// the minimum envelope the UI needs: <c>step</c>, <c>status</c>, and
/// (on failure) <c>code</c>.
/// </para>
/// </remarks>
public class EmitValidationProgressActivity(
    IActivityEventBus activityEventBus,
    ILoggerFactory loggerFactory)
    : WorkflowActivity<EmitValidationProgressActivityInput, bool>
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<EmitValidationProgressActivity>();

    /// <inheritdoc />
    public override async Task<bool> RunAsync(
        WorkflowActivityContext context, EmitValidationProgressActivityInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            var severity = string.Equals(input.Status, "Failed", StringComparison.Ordinal)
                ? ActivitySeverity.Warning
                : ActivitySeverity.Info;

            var details = BuildDetails(input);
            var summary = BuildSummary(input);

            var activityEvent = new ActivityEvent(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow,
                Source: Address.ForIdentity(Address.UnitScheme, input.UnitId),
                EventType: ActivityEventType.ValidationProgress,
                Severity: severity,
                Summary: summary,
                Details: details,
                CorrelationId: null,
                Cost: null);

            await activityEventBus.PublishAsync(activityEvent);

            return true;
        }
        catch (Exception ex)
        {
            // Progress events are diagnostic; never allow a publish failure
            // to derail the workflow. Log and return false so a caller that
            // cares could branch on it — the workflow treats this as
            // fire-and-forget.
            _logger.LogWarning(
                ex,
                "Failed to emit ValidationProgress event for unit {UnitId} step {Step} status {Status}.",
                input.UnitId, input.Step, input.Status);
            return false;
        }
    }

    private static JsonElement BuildDetails(EmitValidationProgressActivityInput input)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("step", input.Step.ToString());
            writer.WriteString("status", input.Status);
            if (!string.IsNullOrEmpty(input.Code))
            {
                writer.WriteString("code", input.Code);
            }
            writer.WriteEndObject();
        }

        // Parse the bytes into a detached JsonDocument so the returned
        // JsonElement owns its own backing memory (the MemoryStream we wrote
        // into goes out of scope here).
        var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string BuildSummary(EmitValidationProgressActivityInput input) =>
        string.IsNullOrEmpty(input.Code)
            ? $"{input.Step} {input.Status}"
            : $"{input.Step} {input.Status} ({input.Code})";
}