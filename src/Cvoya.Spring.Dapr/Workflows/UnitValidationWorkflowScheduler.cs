// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Workflows;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;

using global::Dapr.Workflow;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IUnitValidationWorkflowScheduler"/>. Resolves the
/// unit's persisted execution defaults (<c>image</c>, <c>runtime</c>,
/// <c>model</c>) and its tenant-scoped LLM credential, then schedules a
/// new <c>UnitValidationWorkflow</c> run via <see cref="DaprWorkflowClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// Production DI registers this with <c>TryAddSingleton</c> so the private
/// cloud host can layer tenant-aware scheduling (e.g. per-tenant Dapr app
/// routing) without forking the OSS default.
/// </para>
/// <para>
/// The scheduler runs inside the Worker / API host and is the one place
/// that knows how to compose a <see cref="UnitValidationWorkflowInput"/>
/// from actor-side state. Keeping this resolution out of the actor lets
/// <c>UnitActor</c> stay pure Dapr-actor code: the actor emits an intent
/// ("please schedule validation for unit id X") and the scheduler does the
/// side-effectful plumbing on top of the shared DB and credential resolver.
/// </para>
/// </remarks>
public class UnitValidationWorkflowScheduler(
    DaprWorkflowClient workflowClient,
    IServiceScopeFactory scopeFactory,
    ILoggerFactory loggerFactory) : IUnitValidationWorkflowScheduler
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<UnitValidationWorkflowScheduler>();

    /// <inheritdoc />
    public async Task<UnitValidationSchedule> ScheduleAsync(
        string unitActorId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(unitActorId))
        {
            throw new ArgumentException("Unit actor id must be supplied.", nameof(unitActorId));
        }

        // Both the SpringDbContext (per-request) and the
        // ILlmCredentialResolver (scoped) live behind a fresh DI scope —
        // the scheduler itself is a singleton so it cannot consume either
        // directly through its constructor.
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var credentialResolver = scope.ServiceProvider
            .GetRequiredService<ILlmCredentialResolver>();

        // Look up the unit's user-facing name and persisted Definition
        // document by actor id. The actor keyed by Dapr actor Guid does
        // not know its name; this query is the cheapest join back to the
        // directory row that carries it. AsNoTracking: read path only.
        var entity = await db.UnitDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                u => u.ActorId == unitActorId && u.DeletedAt == null,
                cancellationToken);

        if (entity is null)
        {
            // The directory has the actor id but the canonical row is gone
            // — almost certainly a tear-down race. Surface as a structured
            // configuration failure so the actor doesn't get stuck.
            throw new UnitValidationSchedulingException(new UnitValidationError(
                Step: UnitValidationStep.PullingImage,
                Code: UnitValidationCodes.ConfigurationIncomplete,
                Message: $"No unit definition row exists for actor id '{unitActorId}'. " +
                    "The unit may have been deleted; recreate it before validating.",
                Details: null));
        }

        var defaults = DbUnitExecutionStore.Extract(entity.Definition);
        if (defaults is null)
        {
            // No execution defaults at all — closest semantic step is the
            // first one the workflow would have run (image pull). The
            // operator can fix this from the unit's Execution tab and
            // call /revalidate.
            throw new UnitValidationSchedulingException(new UnitValidationError(
                Step: UnitValidationStep.PullingImage,
                Code: UnitValidationCodes.ConfigurationIncomplete,
                Message: "No execution defaults are configured on this unit. " +
                    "Set a container image (and optionally a runtime) before validation can run.",
                Details: new Dictionary<string, string>
                {
                    ["missing"] = "image,runtime",
                }));
        }

        if (string.IsNullOrWhiteSpace(defaults.Image))
        {
            throw new UnitValidationSchedulingException(new UnitValidationError(
                Step: UnitValidationStep.PullingImage,
                Code: UnitValidationCodes.ConfigurationIncomplete,
                Message: "This unit has no container image configured. " +
                    "Set the image on the unit's Execution tab and retry validation.",
                Details: new Dictionary<string, string>
                {
                    ["missing"] = "image",
                }));
        }

        // Runtime id lives in the execution.runtime slot. The dapr-agent
        // style runtimes use `provider` as their runtime id too (e.g.
        // openai/google); fall back to provider when `runtime` is not set.
        var runtimeId = defaults.Runtime ?? defaults.Provider;
        if (string.IsNullOrWhiteSpace(runtimeId))
        {
            throw new UnitValidationSchedulingException(new UnitValidationError(
                Step: UnitValidationStep.VerifyingTool,
                Code: UnitValidationCodes.ConfigurationIncomplete,
                Message: "This unit has no runtime or provider configured. " +
                    "Pick a runtime (or provider) on the unit's Execution tab and retry validation.",
                Details: new Dictionary<string, string>
                {
                    ["missing"] = "runtime",
                }));
        }

        // Resolve the credential via the two-tier chain (unit → tenant).
        // When the runtime declares CredentialKind.None the resolver
        // returns NotFound and we pass the empty string through — the
        // workflow's RunContainerProbe layer pre-filters "no credential"
        // step for runtimes that do not authenticate.
        var credentialResolution = await credentialResolver
            .ResolveAsync(
                providerId: defaults.Provider ?? runtimeId,
                unitName: entity.UnitId,
                cancellationToken);

        var credential = credentialResolution.Value ?? string.Empty;
        var requestedModel = defaults.Model ?? string.Empty;

        var input = new UnitValidationWorkflowInput(
            UnitId: unitActorId,
            UnitName: entity.UnitId,
            Image: defaults.Image,
            RuntimeId: runtimeId,
            Credential: credential,
            RequestedModel: requestedModel);

        var instanceId = await workflowClient.ScheduleNewWorkflowAsync(
            nameof(UnitValidationWorkflow),
            input: input);

        _logger.LogInformation(
            "Scheduled UnitValidationWorkflow {InstanceId} for unit {UnitName} (actor {ActorId}) image={Image} runtime={Runtime} model={Model}.",
            instanceId, entity.UnitId, unitActorId, defaults.Image, runtimeId, requestedModel);

        return new UnitValidationSchedule(instanceId, entity.UnitId);
    }
}