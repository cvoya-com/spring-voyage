// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Manifest;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IUnitCreationService"/> implementation.
///
/// The raw ingredients (directory register + actor metadata + member routing)
/// are identical to what <see cref="Endpoints.UnitEndpoints.CreateUnitAsync"/>
/// and <see cref="Endpoints.UnitEndpoints.AddMemberAsync"/> used to do inline;
/// this service just packages them so the three create endpoints share a path.
/// </summary>
public class UnitCreationService : IUnitCreationService
{
    private readonly IDirectoryService _directoryService;
    private readonly IActorProxyFactory _actorProxyFactory;
    private readonly MessageRouter _messageRouter;
    private readonly IUnitConnectorConfigStore _connectorConfigStore;
    private readonly IReadOnlyList<IConnectorType> _connectorTypes;
    private readonly ISkillBundleResolver _bundleResolver;
    private readonly ISkillBundleValidator _bundleValidator;
    private readonly IUnitSkillBundleStore _bundleStore;
    private readonly ILogger<UnitCreationService> _logger;

    /// <summary>
    /// Creates a new <see cref="UnitCreationService"/>.
    /// </summary>
    public UnitCreationService(
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        MessageRouter messageRouter,
        IUnitConnectorConfigStore connectorConfigStore,
        IEnumerable<IConnectorType> connectorTypes,
        ISkillBundleResolver bundleResolver,
        ISkillBundleValidator bundleValidator,
        IUnitSkillBundleStore bundleStore,
        ILoggerFactory loggerFactory)
    {
        _directoryService = directoryService;
        _actorProxyFactory = actorProxyFactory;
        _messageRouter = messageRouter;
        _connectorConfigStore = connectorConfigStore;
        _connectorTypes = connectorTypes.ToList();
        _bundleResolver = bundleResolver;
        _bundleValidator = bundleValidator;
        _bundleStore = bundleStore;
        _logger = loggerFactory.CreateLogger<UnitCreationService>();
    }

    /// <inheritdoc />
    public Task<UnitCreationResult> CreateAsync(
        CreateUnitRequest request,
        CancellationToken cancellationToken) =>
        CreateCoreAsync(
            name: request.Name,
            displayName: request.DisplayName,
            description: request.Description,
            model: request.Model,
            color: request.Color,
            members: Array.Empty<MemberManifest>(),
            warnings: new List<string>(),
            connector: request.Connector,
            skillReferences: Array.Empty<SkillBundleReference>(),
            cancellationToken);

    /// <inheritdoc />
    public Task<UnitCreationResult> CreateFromManifestAsync(
        UnitManifest manifest,
        UnitCreationOverrides overrides,
        CancellationToken cancellationToken,
        UnitConnectorBindingRequest? connector = null)
    {
        var name = manifest.Name!;
        var displayName = !string.IsNullOrWhiteSpace(overrides.DisplayName)
            ? overrides.DisplayName!
            : name;
        var description = manifest.Description ?? string.Empty;
        var model = overrides.Model
            ?? manifest.Ai?.Model;
        var color = overrides.Color;

        var warnings = new List<string>();
        foreach (var section in ManifestParser.CollectUnsupportedSections(manifest))
        {
            warnings.Add(
                $"section '{section}' is parsed but not yet applied");
        }

        return CreateCoreAsync(
            name,
            displayName,
            description,
            model,
            color,
            manifest.Members ?? new List<MemberManifest>(),
            warnings,
            connector,
            ExtractSkillReferences(manifest),
            cancellationToken);
    }

    private static IReadOnlyList<SkillBundleReference> ExtractSkillReferences(UnitManifest manifest)
    {
        var references = manifest.Ai?.Skills;
        if (references is null || references.Count == 0)
        {
            return Array.Empty<SkillBundleReference>();
        }

        var list = new List<SkillBundleReference>(references.Count);
        foreach (var r in references)
        {
            if (string.IsNullOrWhiteSpace(r.Package) || string.IsNullOrWhiteSpace(r.Skill))
            {
                continue;
            }
            list.Add(new SkillBundleReference(r.Package!, r.Skill!));
        }
        return list;
    }

    private async Task<UnitCreationResult> CreateCoreAsync(
        string name,
        string displayName,
        string description,
        string? model,
        string? color,
        IReadOnlyList<MemberManifest> members,
        List<string> warnings,
        UnitConnectorBindingRequest? connector,
        IReadOnlyList<SkillBundleReference> skillReferences,
        CancellationToken cancellationToken)
    {
        // Validate the connector binding request up-front — before we touch
        // any server-side state — so the caller sees a 400/404 without a
        // rollback dance happening under the hood.
        IConnectorType? targetConnector = null;
        if (connector is not null)
        {
            targetConnector = ResolveConnectorType(connector);
        }

        // Resolve skill bundles and validate their tool requirements up-front
        // as well. Any failure here surfaces to the caller as a typed
        // exception that the endpoint layer maps to a ProblemDetails 4xx so
        // the manifest author sees the exact bundle / tool that rejected
        // creation before we write any state.
        IReadOnlyList<SkillBundle> resolvedBundles = Array.Empty<SkillBundle>();
        if (skillReferences.Count > 0)
        {
            resolvedBundles = await ResolveSkillBundlesAsync(skillReferences, cancellationToken);
            await _bundleValidator.ValidateAsync(name, resolvedBundles, cancellationToken);
        }

        var actorId = Guid.NewGuid().ToString();
        var address = new Address("unit", name);
        var entry = new DirectoryEntry(
            address,
            actorId,
            displayName,
            description,
            null,
            DateTimeOffset.UtcNow);

        await _directoryService.RegisterAsync(entry, cancellationToken);

        try
        {
            // DisplayName/Description live on the directory entity; only forward
            // the actor-owned fields (Model, Color) to the metadata write to avoid
            // a double-write — mirrors UnitEndpoints.CreateUnitAsync.
            var metadata = new UnitMetadata(
                DisplayName: null,
                Description: null,
                Model: model,
                Color: color);

            if (metadata.Model is not null || metadata.Color is not null)
            {
                var proxy = _actorProxyFactory.CreateActorProxy<IUnitActor>(
                    new ActorId(actorId), nameof(IUnitActor));
                await proxy.SetMetadataAsync(metadata, cancellationToken);
            }

            var membersAdded = 0;
            foreach (var member in members)
            {
                var resolved = ResolveMemberAddress(member);
                if (resolved is null)
                {
                    warnings.Add("member entry had no 'agent' or 'unit' field; skipped");
                    continue;
                }

                var payload = JsonSerializer.SerializeToElement(new
                {
                    Action = "AddMember",
                    MemberScheme = resolved.Value.Scheme,
                    MemberPath = resolved.Value.Path,
                });

                var message = new Message(
                    Guid.NewGuid(),
                    new Address("human", "api"),
                    address,
                    MessageType.Domain,
                    null,
                    payload,
                    DateTimeOffset.UtcNow);

                var result = await _messageRouter.RouteAsync(message, cancellationToken);
                if (!result.IsSuccess)
                {
                    warnings.Add(
                        $"failed to add member {resolved.Value.Scheme}:{resolved.Value.Path}: {result.Error!.Message}");
                    continue;
                }
                membersAdded++;
            }

            // Persist the resolved skill bundles so prompt assembly can
            // rehydrate them on every message turn without reparsing the
            // manifest. Writes happen after the directory register so we
            // never leave bundle rows behind an un-discoverable unit.
            if (resolvedBundles.Count > 0)
            {
                await _bundleStore.SetAsync(name, resolvedBundles, cancellationToken);
            }

            // Bind the connector *after* the actor is reachable — the store
            // talks to the unit actor, which needs the directory entry in
            // place. A failure here rolls the whole creation back (below)
            // so the user never sees a half-configured unit.
            if (targetConnector is not null)
            {
                try
                {
                    await _connectorConfigStore.SetAsync(
                        name,
                        targetConnector.TypeId,
                        connector!.Config,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    throw new UnitCreationBindingException(
                        UnitCreationBindingFailureReason.StoreFailure,
                        $"Failed to bind unit '{name}' to connector '{targetConnector.Slug}': {ex.Message}",
                        ex);
                }
            }

            var response = new UnitResponse(
                entry.ActorId,
                entry.Address.Path,
                entry.DisplayName,
                entry.Description,
                entry.RegisteredAt,
                UnitStatus.Draft,
                metadata.Model,
                metadata.Color);

            return new UnitCreationResult(response, warnings, membersAdded);
        }
        catch (UnitCreationBindingException)
        {
            await TryRollbackAsync(address, name, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Best-effort rollback: unregisters the directory entry so the caller's
    /// failed creation leaves nothing behind. We deliberately do NOT touch
    /// the actor — absent a directory entry, its state is unreachable and
    /// will be cleared on the next actor reactivation. Unit-scoped secrets
    /// are not yet provisioned at this point (the connector binding is the
    /// last step), so no additional cleanup is needed.
    /// </summary>
    private async Task TryRollbackAsync(Address address, string name, CancellationToken ct)
    {
        try
        {
            await _directoryService.UnregisterAsync(address, ct);
        }
        catch (Exception ex)
        {
            // Surface but don't mask the original failure — the binding
            // exception is about to be rethrown. The operator sees both in
            // the logs.
            _logger.LogWarning(ex,
                "Rollback failed: could not unregister directory entry for unit '{UnitName}' after connector-binding failure. Manual cleanup may be required.",
                name);
        }

        // Best-effort bundle cleanup too — we may have persisted bundle rows
        // before the binding failure. A missing row is a no-op in the store.
        try
        {
            await _bundleStore.DeleteAsync(name, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Rollback failed: could not delete skill-bundle rows for unit '{UnitName}' after connector-binding failure.",
                name);
        }
    }

    /// <summary>
    /// Resolves every <see cref="SkillBundleReference"/> in declaration order,
    /// wrapping resolver exceptions in <see cref="SkillBundleValidationException"/>
    /// so the endpoint layer surfaces them through a single ProblemDetails
    /// mapping. The bundle order is preserved — the prompt layer concatenates
    /// prompts in declaration order per <c>docs/architecture/packages.md</c>.
    /// </summary>
    private async Task<IReadOnlyList<SkillBundle>> ResolveSkillBundlesAsync(
        IReadOnlyList<SkillBundleReference> references,
        CancellationToken ct)
    {
        var resolved = new List<SkillBundle>(references.Count);
        foreach (var reference in references)
        {
            resolved.Add(await _bundleResolver.ResolveAsync(reference, ct));
        }
        return resolved;
    }

    /// <summary>
    /// Looks up the requested connector type by id (preferred) or slug, and
    /// throws a typed exception when neither resolves. Also rejects requests
    /// that supply neither identifier.
    /// </summary>
    private IConnectorType ResolveConnectorType(UnitConnectorBindingRequest connector)
    {
        if (connector.TypeId == Guid.Empty && string.IsNullOrWhiteSpace(connector.TypeSlug))
        {
            throw new UnitCreationBindingException(
                UnitCreationBindingFailureReason.InvalidBindingRequest,
                "Connector binding requires either 'typeId' or 'typeSlug'.");
        }

        if (connector.TypeId != Guid.Empty)
        {
            var byId = _connectorTypes.FirstOrDefault(c => c.TypeId == connector.TypeId);
            if (byId is not null)
            {
                return byId;
            }
        }

        if (!string.IsNullOrWhiteSpace(connector.TypeSlug))
        {
            var bySlug = _connectorTypes.FirstOrDefault(
                c => string.Equals(c.Slug, connector.TypeSlug, StringComparison.OrdinalIgnoreCase));
            if (bySlug is not null)
            {
                return bySlug;
            }
        }

        var identifier = connector.TypeId != Guid.Empty
            ? connector.TypeId.ToString()
            : connector.TypeSlug!;
        throw new UnitCreationBindingException(
            UnitCreationBindingFailureReason.UnknownConnectorType,
            $"Connector '{identifier}' is not registered on this server.");
    }

    private static (string Scheme, string Path)? ResolveMemberAddress(MemberManifest member)
    {
        if (!string.IsNullOrWhiteSpace(member.Agent))
        {
            return ("agent", member.Agent!);
        }
        if (!string.IsNullOrWhiteSpace(member.Unit))
        {
            return ("unit", member.Unit!);
        }
        return null;
    }
}