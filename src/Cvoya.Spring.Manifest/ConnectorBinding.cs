// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;
using System.Text.Json;

/// <summary>
/// Operator-supplied configuration for a single connector at install time
/// (#1671). Carries the connector slug plus the typed config payload the
/// connector understands. The shape is generic over slug — only the
/// operator (and the connector's own wizard / CLI shorthand) knows the
/// exact keys for any given connector type.
/// </summary>
/// <remarks>
/// <para>
/// The install pipeline does not reach into <see cref="Config"/> beyond
/// forwarding it to the connector's <c>IUnitConnectorConfigStore</c>. The
/// connector type's <c>ConfigType</c> dictates the schema; the install
/// pipeline stays type-agnostic so adding a new connector is a single
/// DI registration without touching the install plumbing.
/// </para>
/// <para>
/// <see cref="Slug"/> is the connector slug (e.g. <c>github</c>) — not the
/// connector type Guid. The pipeline resolves the slug to a
/// <c>IConnectorType</c> via the registry and reads its <c>TypeId</c> when
/// it needs to persist the binding through <c>IUnitConnectorConfigStore</c>.
/// </para>
/// </remarks>
/// <param name="Slug">Connector type slug, matching <c>IConnectorType.Slug</c>.</param>
/// <param name="Config">
/// Typed config payload. The shape is dictated by the target connector's
/// <c>ConfigType</c>; the install pipeline stays type-agnostic.
/// </param>
public sealed record ConnectorBinding(string Slug, JsonElement Config);

/// <summary>
/// Pre-flight gap in the install request — the request asked to install
/// a package whose manifest declares a required connector, but the caller
/// did not supply a binding for it. Surfaced through
/// <see cref="ConnectorBindingsMissingException"/> as a structured 400 so
/// the wizard / CLI can render a precise error per missing slug.
/// </summary>
/// <param name="Slug">The connector slug that has no binding.</param>
/// <param name="Scope">
/// Where the binding is missing — <c>"package"</c> for the package-level
/// inheritance default, or <c>"unit"</c> when a member unit declared
/// <c>inherit: false</c> and has no override.
/// </param>
/// <param name="UnitName">
/// Member unit name when <see cref="Scope"/> is <c>"unit"</c>; <c>null</c>
/// otherwise.
/// </param>
public sealed record ConnectorBindingMissing(
    string Slug,
    string Scope,
    string? UnitName);

/// <summary>
/// Thrown by the install pipeline when one or more required connector
/// bindings are missing from the install request (#1671). The caller
/// (HTTP endpoint, CLI) projects each entry into the
/// <c>ConnectorBindingMissing</c> wire shape and returns
/// <c>400 Bad Request</c> with the list. No DB writes occur — the
/// pre-flight runs entirely before Phase 1.
/// </summary>
public sealed class ConnectorBindingsMissingException : System.Exception
{
    /// <summary>Initialises a new <see cref="ConnectorBindingsMissingException"/>.</summary>
    public ConnectorBindingsMissingException(IReadOnlyList<ConnectorBindingMissing> missing)
        : base(BuildMessage(missing))
    {
        Missing = missing;
    }

    /// <summary>The structured list of missing bindings.</summary>
    public IReadOnlyList<ConnectorBindingMissing> Missing { get; }

    private static string BuildMessage(IReadOnlyList<ConnectorBindingMissing> missing)
    {
        var parts = new List<string>(missing.Count);
        foreach (var m in missing)
        {
            parts.Add(m.Scope == "unit" && m.UnitName is { Length: > 0 }
                ? $"unit '{m.UnitName}' requires connector '{m.Slug}' (no binding supplied)"
                : $"package requires connector '{m.Slug}' (no binding supplied)");
        }
        return "ConnectorBindingMissing: " + string.Join("; ", parts);
    }
}

/// <summary>
/// Thrown by the install pipeline when an install request supplies a
/// connector binding for a slug the package does not declare in its
/// <c>connectors:</c> block (#1671). Surfaced as 400.
/// </summary>
public sealed class UnknownConnectorSlugException : System.Exception
{
    /// <summary>Initialises a new <see cref="UnknownConnectorSlugException"/>.</summary>
    public UnknownConnectorSlugException(string slug, string scope, string? unitName = null)
        : base(BuildMessage(slug, scope, unitName))
    {
        Slug = slug;
        Scope = scope;
        UnitName = unitName;
    }

    /// <summary>The connector slug that does not appear in any package declaration.</summary>
    public string Slug { get; }

    /// <summary>"package" or "unit".</summary>
    public string Scope { get; }

    /// <summary>The unit name when <see cref="Scope"/> is "unit"; null otherwise.</summary>
    public string? UnitName { get; }

    private static string BuildMessage(string slug, string scope, string? unitName)
        => scope == "unit" && unitName is { Length: > 0 }
            ? $"Connector '{slug}' is not declared by any unit named '{unitName}' in this package."
            : $"Connector '{slug}' is not declared by this package's 'connectors:' block.";
}