// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Linq;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Manifest;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Pure credential-binding resolver (#2159). Walks the resolved
/// package's units, derives every distinct
/// <c>(provider, authMethod)</c> credential edge consumed by a member
/// unit's runtime, and reports which ones are not satisfied by the
/// supplied credential bindings. Tenant-scope inheritance (i.e. is the
/// secret already in the tenant secret store?) is checked separately by
/// the install service against <c>ILlmCredentialResolver</c> — the
/// resolver here is pure so it stays trivially unit-testable.
/// </summary>
/// <remarks>
/// <para>
/// The resolver does not invent a new package-level schema. Required
/// credentials are <i>derived</i> from each member unit's
/// <c>ai.runtime</c> + <c>ai.model.provider</c> against the runtime
/// catalogue's per-edge <c>authMethod</c> + <c>credentialEnvVar</c>.
/// Mirror of the existing connector-binding pattern (#1671 / ADR-0037
/// D3): the package YAML stays clean, and credentials added in the
/// catalogue automatically become required by every consuming unit.
/// </para>
/// <para>
/// Edges with <c>authMethod is null</c> (e.g. the
/// <c>spring-voyage</c> + <c>ollama</c> edge) require no credential and
/// are skipped entirely.
/// </para>
/// </remarks>
public static class CredentialBindingResolver
{
    /// <summary>
    /// Computes the consumption set for a single resolved package: every
    /// distinct <c>(provider, authMethod)</c> edge a member unit needs,
    /// plus the list of consuming unit names per edge.
    /// </summary>
    public static IReadOnlyList<RequiredCredential> CollectRequired(
        ResolvedPackage package,
        IRuntimeCatalog runtimeCatalog)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(runtimeCatalog);

        var byEdge = new Dictionary<EdgeKey, RequiredCredentialBuilder>(EdgeKey.Comparer);

        foreach (var unit in package.Units.Where(u => !u.IsCrossPackage))
        {
            var (runtimeId, providerId) = ExtractRuntimeAndProvider(unit);
            if (runtimeId is null || providerId is null)
            {
                // Unit doesn't declare an LLM (template / partial / not yet
                // wired). Skip — it cannot consume a credential.
                continue;
            }

            var runtime = runtimeCatalog.GetAgentRuntime(runtimeId);
            var edge = runtime?.ModelProviders
                .FirstOrDefault(e => string.Equals(e.Id, providerId, StringComparison.OrdinalIgnoreCase));
            if (edge is null || edge.AuthMethod is null || edge.CredentialEnvVar is null)
            {
                // Unknown runtime / provider, or no-credential edge
                // (Ollama). Either case: no credential to require.
                continue;
            }

            var key = new EdgeKey(providerId.ToLowerInvariant(), edge.AuthMethod.Value);
            if (!byEdge.TryGetValue(key, out var builder))
            {
                builder = new RequiredCredentialBuilder(
                    Provider: key.Provider,
                    AuthMethod: key.AuthMethod,
                    SecretName: CredentialNaming.SecretNameFor(key.Provider, key.AuthMethod),
                    CredentialEnvVar: edge.CredentialEnvVar,
                    ConsumingUnits: new List<string>());
                byEdge[key] = builder;
            }

            builder.ConsumingUnits.Add(unit.Name);
        }

        return byEdge.Values
            .Select(b => new RequiredCredential(
                b.Provider, b.AuthMethod, b.SecretName, b.CredentialEnvVar, b.ConsumingUnits))
            .ToList();
    }

    /// <summary>
    /// Pre-flight: matches the package's required credentials against
    /// the operator-supplied bindings, and reports unsupplied ones plus
    /// any bindings that don't correspond to a real consumer.
    /// </summary>
    /// <remarks>
    /// Tenant-scope inheritance is <b>not</b> evaluated here — that
    /// requires async DB access and is handled by the install service
    /// after this pure resolver runs. Each unsupplied entry returned
    /// from this resolver is a <i>candidate</i> gap; the service then
    /// asks <c>ILlmCredentialResolver</c> whether the tenant store
    /// already has the secret and removes it from the gap list when so.
    /// </remarks>
    public static CredentialBindingResolution Resolve(
        IReadOnlyList<RequiredCredential> required,
        IReadOnlyList<CredentialBinding>? supplied)
    {
        ArgumentNullException.ThrowIfNull(required);

        var suppliedByEdge = new Dictionary<EdgeKey, CredentialBinding>(EdgeKey.Comparer);
        var unknown = new List<UnknownCredentialEdgeEntry>();

        foreach (var binding in supplied ?? Array.Empty<CredentialBinding>())
        {
            var key = new EdgeKey(
                (binding.Provider ?? string.Empty).Trim().ToLowerInvariant(),
                binding.AuthMethod);
            if (!required.Any(r => string.Equals(r.Provider, key.Provider, StringComparison.OrdinalIgnoreCase)
                                   && r.AuthMethod == key.AuthMethod))
            {
                unknown.Add(new UnknownCredentialEdgeEntry(key.Provider, key.AuthMethod));
                continue;
            }
            // Last write wins on duplicates — the operator probably
            // pasted the same secret twice; surfacing it as an error
            // would be hostile UX.
            suppliedByEdge[key] = binding;
        }

        var unsuppliedCandidates = new List<RequiredCredential>();
        var resolved = new List<ResolvedCredentialBinding>();

        foreach (var req in required)
        {
            var key = new EdgeKey(req.Provider, req.AuthMethod);
            if (suppliedByEdge.TryGetValue(key, out var binding))
            {
                resolved.Add(new ResolvedCredentialBinding(req, binding));
            }
            else
            {
                unsuppliedCandidates.Add(req);
            }
        }

        return new CredentialBindingResolution(resolved, unsuppliedCandidates, unknown);
    }

    /// <summary>
    /// Project a <see cref="RequiredCredential"/> to the wire shape used
    /// by <see cref="CredentialsMissingException"/> when the install
    /// service has confirmed the gap is real (no tenant fallback).
    /// </summary>
    public static CredentialMissing ToMissing(RequiredCredential req) =>
        new(req.Provider, req.AuthMethod, req.SecretName, req.CredentialEnvVar,
            Scope: "package", UnitName: null, ConsumingUnits: req.ConsumingUnits);

    private static (string? Runtime, string? Provider) ExtractRuntimeAndProvider(ResolvedArtefact unit)
    {
        if (string.IsNullOrEmpty(unit.Content))
        {
            return (null, null);
        }

        // Best-effort parse: the package validator already accepted the
        // YAML; we just want the (runtime, provider) pair from the `ai:`
        // block. A malformed unit at this point has bigger problems and
        // will fail elsewhere — return (null, null) so it's silently
        // skipped here.
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var manifest = deserializer.Deserialize<UnitManifest>(unit.Content);
            return (manifest?.Ai?.Runtime?.Trim(), manifest?.Ai?.Model?.Provider?.Trim());
        }
        catch
        {
            return (null, null);
        }
    }

    private readonly record struct EdgeKey(string Provider, AuthMethod AuthMethod)
    {
        public static readonly IEqualityComparer<EdgeKey> Comparer = EqualityComparer<EdgeKey>.Default;
    }

    private sealed record RequiredCredentialBuilder(
        string Provider,
        AuthMethod AuthMethod,
        string SecretName,
        string CredentialEnvVar,
        List<string> ConsumingUnits);
}

/// <summary>
/// One distinct credential edge a package's units consume.
/// </summary>
public sealed record RequiredCredential(
    string Provider,
    AuthMethod AuthMethod,
    string SecretName,
    string CredentialEnvVar,
    IReadOnlyList<string> ConsumingUnits);

/// <summary>
/// Output of the pure pre-flight match against operator-supplied bindings.
/// Tenant-scope inheritance is layered on top by the install service.
/// </summary>
public sealed record CredentialBindingResolution(
    IReadOnlyList<ResolvedCredentialBinding> Resolved,
    IReadOnlyList<RequiredCredential> UnsuppliedCandidates,
    IReadOnlyList<UnknownCredentialEdgeEntry> UnknownEdges);

/// <summary>
/// One required credential with the operator-supplied value attached.
/// </summary>
public sealed record ResolvedCredentialBinding(
    RequiredCredential Required,
    CredentialBinding Binding);

/// <summary>
/// One credential supplied by the operator that does not match any
/// edge consumed by a member unit — surfaced as a structural-error
/// 400 mirroring <see cref="UnknownConnectorBindingEntry"/>.
/// </summary>
public sealed record UnknownCredentialEdgeEntry(string Provider, AuthMethod AuthMethod);
