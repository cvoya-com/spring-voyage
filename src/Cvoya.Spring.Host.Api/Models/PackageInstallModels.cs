// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System;
using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// Request body for <c>POST /api/v1/packages/install</c>.
/// Accepts one or more install targets as a batch (ADR-0035 decision 14).
/// Single-package install = array-of-one.
/// </summary>
/// <param name="Targets">
/// The packages to install, each with an optional input map.
/// All packages in one request are installed as a single atomic batch:
/// Phase 1 commits all rows or rolls all back; Phase 2 activates in
/// dependency order.
/// </param>
public sealed record PackageInstallRequest(
    IReadOnlyList<PackageInstallTarget> Targets);

/// <summary>
/// A single package within a <see cref="PackageInstallRequest"/>.
/// </summary>
/// <param name="PackageName">
/// The package name. Must match <c>metadata.name</c> in the package YAML
/// (for catalog installs this is the catalog key; for file uploads the
/// YAML is supplied separately and this field is ignored if the YAML
/// declares its own name).
/// </param>
/// <param name="Inputs">
/// Key/value input overrides for this package. Keys must match the
/// <c>inputs</c> schema declared in the <c>package.yaml</c>. Secret-typed
/// inputs must already be in <c>secret://</c> reference form. Null is
/// treated as an empty map.
/// </param>
/// <param name="ConnectorBindings">
/// Optional connector binding payload (#1671). Carries the operator-
/// supplied configuration for each connector the package declares in its
/// <c>connectors:</c> block. Empty when the package declares no
/// connectors. The pre-flight validator returns 400 with a structured
/// list of <see cref="ConnectorBindingMissingDetail"/> entries when a
/// required connector has no binding.
/// </param>
/// <param name="IntoUnit">
/// Optional install-scope binding (ADR-0043 §6). When set, the package's
/// top-level artefacts (Units / Agents directly under
/// <c>packages/&lt;pkg&gt;/units/</c> or <c>packages/&lt;pkg&gt;/agents/</c>)
/// are bound to the named unit instead of the tenant — top-level
/// agents become members of that unit, top-level units become its
/// sub-units. Accepts either a Guid or a display name; <c>"tenant"</c>
/// is the explicit form of the default (top-level artefacts bind to
/// the tenant). The package's internal structure (a top-level unit's
/// own members, an agent's own skills) is unaffected.
/// </param>
/// <param name="DisplayName">
/// Optional display-name override for the package's single top-level
/// activatable (#2310). When set, replaces the artefact's <c>name:</c>
/// field for display purposes — useful when installing the same package
/// multiple times so the operator can distinguish the instances in the
/// UI. Identity is always a fresh Guid; the override only changes the
/// display name. Packages that ship multiple top-level activatables
/// reject the override with a 400
/// (<c>code: AmbiguousDisplayName</c>).
/// </param>
/// <param name="HumanOverrides">
/// Optional per-declaration <c>Human → TenantUser</c> binding overrides
/// (ADR-0062 § 6, #2822). Keyed by the <c>- human:</c> declaration's
/// <c>displayName</c> (case-sensitive) within this package. When a
/// declaration's display-name key matches, the supplied
/// <see cref="PackageHumanOverride.TenantUserRef"/> wins over
/// <c>ITenantUserDefaultResolver</c>; declarations without a matching
/// override fall through to the resolver. The CLI's <c>spring package
/// install --as-human &lt;display-name&gt;=&lt;tenant-user-ref&gt;</c> flag
/// surfaces this. Anonymous declarations (no <c>displayName</c>) cannot
/// be overridden and always flow through the resolver. An override
/// referencing a tenant user that does not exist in the current tenant
/// surfaces a structured 400 naming the offending declaration.
/// </param>
public sealed record PackageInstallTarget(
    string PackageName,
    IReadOnlyDictionary<string, string>? Inputs,
    PackageConnectorBindings? ConnectorBindings = null,
    string? Version = null,
    IReadOnlyList<CredentialBindingPayload>? Credentials = null,
    string? IntoUnit = null,
    string? DisplayName = null,
    IReadOnlyDictionary<string, PackageHumanOverride>? HumanOverrides = null);

/// <summary>
/// One per-declaration <c>Human → TenantUser</c> binding override on a
/// <see cref="PackageInstallTarget.HumanOverrides"/> map (ADR-0062 § 6,
/// #2822). The map key is the <c>- human:</c> declaration's
/// <c>displayName</c> within the package; this payload carries the
/// override target.
/// </summary>
/// <param name="TenantUserRef">
/// The target <c>TenantUser</c> id (dashed or no-dash hex form). The
/// server validates that the id exists in the current tenant; a non-
/// matching id surfaces a structured 400 naming the offending
/// declaration. The CLI accepts a Guid, the literal <c>me</c> (=
/// authenticated caller), or an OAuth subject; the latter two forms
/// are resolved on the CLI before this wire field is populated.
/// </param>
public sealed record PackageHumanOverride(
    Guid TenantUserRef);

/// <summary>
/// Operator-supplied connector bindings for a single package install
/// target (#1671). Two scopes:
/// <list type="bullet">
///   <item><description>
///     <c>package.&lt;slug&gt;</c> — package-scope binding inherited by
///     every member unit unless the unit's manifest opts out.
///   </description></item>
///   <item><description>
///     <c>units.&lt;unit-name&gt;.&lt;slug&gt;</c> — per-unit override
///     binding.
///   </description></item>
/// </list>
/// </summary>
/// <param name="Package">Package-scope bindings keyed by connector slug.</param>
/// <param name="Units">Unit-scope bindings keyed by unit name then slug.</param>
public sealed record PackageConnectorBindings(
    IReadOnlyDictionary<string, ConnectorBindingPayload>? Package,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, ConnectorBindingPayload>>? Units);

/// <summary>
/// Wire shape for one connector binding. The <c>config</c> payload is
/// opaque to the install pipeline — its schema is dictated by the
/// connector's <c>ConfigType</c>.
/// </summary>
/// <param name="Config">Connector-typed config payload.</param>
public sealed record ConnectorBindingPayload(JsonElement Config);

/// <summary>
/// One missing connector binding surfaced through the
/// <c>ConnectorBindingMissing</c> 400 (#1671). Carried in the response's
/// <c>extensions["missing"]</c> array so the wizard / CLI can render a
/// precise per-slug error rather than free-text.
/// </summary>
/// <param name="Slug">The connector slug the binding is missing for.</param>
/// <param name="Scope">"package" or "unit".</param>
/// <param name="UnitName">Member unit name when <see cref="Scope"/> is "unit"; <c>null</c> otherwise.</param>
public sealed record ConnectorBindingMissingDetail(
    string Slug,
    string Scope,
    string? UnitName);

/// <summary>
/// Wire shape for one operator-supplied LLM credential at install time
/// (#2159). The install pipeline writes accepted bindings as
/// tenant-scoped secrets during Phase 2, keyed by
/// <c>{provider}-{authMethod-slug}</c> per
/// <see cref="Cvoya.Spring.Core.Catalog.CredentialNaming.SecretNameFor"/>.
/// </summary>
/// <param name="Provider">Provider id — <c>anthropic</c>, <c>openai</c>, <c>google</c>.</param>
/// <param name="AuthMethod">Auth method on the consuming runtime/provider edge — <c>oauth</c> or <c>api-key</c>.</param>
/// <param name="Value">The cleartext secret value the operator typed. Never persisted as plaintext beyond the request.</param>
public sealed record CredentialBindingPayload(
    string Provider,
    string AuthMethod,
    string Value);

/// <summary>
/// One missing LLM credential surfaced through the
/// <c>CredentialsMissing</c> 400 (#2159). Carried in the response's
/// <c>extensions["missing"]</c> array so the wizard / CLI can prompt
/// for the missing values precisely.
/// </summary>
/// <param name="Provider">The provider that needs the credential.</param>
/// <param name="AuthMethod">The auth method on the consuming edge — <c>oauth</c> or <c>api-key</c>.</param>
/// <param name="SecretName">Canonical secret name the resolver looks for.</param>
/// <param name="CredentialEnvVar">Env var the runtime launcher consumes the resolved value under.</param>
/// <param name="Scope">Where the gap was detected — <c>"package"</c> or <c>"unit"</c>.</param>
/// <param name="UnitName">Member unit name when <see cref="Scope"/> is <c>"unit"</c>; <c>null</c> otherwise.</param>
/// <param name="ConsumingUnits">Member units whose runtime/provider edge consumes this credential.</param>
public sealed record CredentialMissingDetail(
    string Provider,
    string AuthMethod,
    string SecretName,
    string CredentialEnvVar,
    string Scope,
    string? UnitName,
    IReadOnlyList<string> ConsumingUnits);

/// <summary>
/// One missing execution-configuration field surfaced through the
/// <c>ConfigurationIncomplete</c> 400 (#1679). Carried in the response's
/// <c>extensions["missing"]</c> array so the wizard / CLI can render a
/// precise per-unit error rather than free-text.
/// </summary>
/// <param name="UnitName">The member unit that has no resolvable execution defaults.</param>
/// <param name="Field">
/// The missing execution field. Always <c>"image"</c> for v0.1 — the
/// only field the validator hard-requires; future required fields can
/// extend the payload without breaking existing callers.
/// </param>
public sealed record ExecutionConfigurationMissingDetail(
    string UnitName,
    string Field);

/// <summary>
/// Response body for <c>POST /api/v1/packages/install</c> and
/// <c>GET /api/v1/installs/{id}</c>.
/// Carries the shared batch identifier and per-package outcome.
/// </summary>
/// <param name="InstallId">
/// The batch identifier. Use this value as <c>{id}</c> in
/// <c>GET /api/v1/installs/{id}</c> and <c>/abort</c>.
/// </param>
/// <param name="Status">
/// Aggregate status: <c>active</c> when all packages succeeded,
/// <c>staging</c> while Phase 2 is in progress, <c>failed</c> if any
/// package failed Phase 2 activation.
/// </param>
/// <param name="Packages">Per-package outcomes.</param>
/// <param name="StartedAt">UTC timestamp when Phase 1 began.</param>
/// <param name="CompletedAt">
/// UTC timestamp when Phase 2 finished (null if still in progress).
/// </param>
/// <param name="Error">
/// Top-level error message for Phase-1 failures. Null for Phase-2 failures
/// (per-package errors are in <see cref="Packages"/>).
/// </param>
public sealed record InstallStatusResponse(
    Guid InstallId,
    string Status,
    IReadOnlyList<InstallPackageDetail> Packages,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Error);

/// <summary>
/// Per-package detail within an <see cref="InstallStatusResponse"/>.
/// </summary>
/// <param name="PackageName">The package name.</param>
/// <param name="State">
/// Current state of this package: <c>staging</c>, <c>active</c>, or
/// <c>failed</c>.
/// </param>
/// <param name="ErrorMessage">
/// Activation error detail when <paramref name="State"/> is <c>failed</c>.
/// </param>
/// <param name="CreatedUnitNames">
/// Names of units this package install created. Empty when the package
/// declared no units. Used by clients (wizard, CLI) to take post-install
/// actions like auto-starting the units.
/// </param>
/// <param name="CreatedAgentIds">
/// Ids of agents this package install created. Empty when the package
/// declared no agents. Used by clients (wizard, CLI) to take post-install
/// actions like auto-deploying persistent agents.
/// </param>
public sealed record InstallPackageDetail(
    string PackageName,
    string State,
    string? ErrorMessage,
    IReadOnlyList<string>? CreatedUnitNames = null,
    IReadOnlyList<string>? CreatedAgentIds = null);
