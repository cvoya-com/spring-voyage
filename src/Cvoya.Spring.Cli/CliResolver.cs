// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.Generated.Models;

/// <summary>
/// Resolves a CLI argument that may be either a Guid (no-dash 32-hex or
/// dashed) or a display-name search to a single canonical Guid.
///
/// Per the single-identity model locked in #1629 (final design comment),
/// every <c>show</c> command on a tenant entity accepts both forms:
///
/// <list type="bullet">
///   <item><description>
///   <c>spring agent show 8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7</c> — direct lookup,
///   exactly one result or 404.
///   </description></item>
///   <item><description>
///   <c>spring agent show alice [--unit engineering]</c> — search by
///   <c>display_name</c> (optionally constrained to a parent unit). Result
///   is 0 / 1 / n; the caller surfaces the disambiguation list on n.
///   </description></item>
/// </list>
///
/// The resolver itself is API-only: every lookup round-trips through the
/// public <see cref="SpringApiClient"/>. There is no CLI-private cache or
/// shortcut — this matches CONVENTIONS.md § "UI / CLI Feature Parity" and
/// the issue's "every user-facing CLI command goes through the public API"
/// rule.
/// </summary>
public sealed class CliResolver
{
    private readonly SpringApiClient _client;

    public CliResolver(SpringApiClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    /// <summary>
    /// Tries to parse <paramref name="value"/> as a Guid. Accepts both the
    /// canonical no-dash 32-hex form and the dashed form so existing
    /// copy-paste workflows from older clients keep working — matches the
    /// <c>GuidFormatter</c> contract on the server side (#1629 comment §3).
    /// </summary>
    public static bool TryParseGuid(string value, out Guid id)
        => Guid.TryParse(value?.Trim() ?? string.Empty, out id);

    /// <summary>
    /// Resolves an agent argument to a Guid. When <paramref name="idOrName"/>
    /// parses as a Guid, the call short-circuits — the caller gets the same
    /// id back and no API round-trip is spent on a name search.
    ///
    /// Otherwise the resolver lists agents and matches by
    /// <c>display_name</c> (case-insensitive, exact). When
    /// <paramref name="unitContext"/> is non-null the candidate set is
    /// further constrained to agents that are members of that unit.
    /// </summary>
    /// <exception cref="CliResolutionException">
    /// Raised on 0-match (no candidate) or n-match (multiple candidates).
    /// The caller renders the message + disambiguation list and exits
    /// non-zero.
    /// </exception>
    public async Task<Guid> ResolveAgentAsync(
        string idOrName,
        Guid? unitContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            throw new CliResolutionException(
                CliEntityKind.Agent,
                idOrName ?? string.Empty,
                unitContext,
                Array.Empty<CliResolutionCandidate>());
        }

        if (TryParseGuid(idOrName, out var direct))
        {
            return direct;
        }

        // #1649: server-side `display_name` + `unit_id` filtering. A current
        // server narrows the candidate set before transmission so this call
        // is O(matches) on the wire and a single round-trip for the
        // unit-context branch (no separate memberships fetch).
        //
        // An older server (pre-#1649) ignores the query params and returns
        // the full agent list — the post-filter below stays as a defensive
        // fallback so the resolver behaves identically against both wire
        // versions. The fallback is the same case-insensitive equality the
        // server applies, so no behavioural drift between client and server.
        var agents = await _client.ListAgentsAsync(
            displayName: idOrName,
            unitId: unitContext,
            ct: ct);

        var matches = new List<AgentResponse>();
        foreach (var agent in agents)
        {
            if (agent.Id is not Guid aid)
            {
                continue;
            }

            if (MatchesDisplayName(agent.DisplayName, idOrName)
                || MatchesDisplayName(agent.Name, idOrName))
            {
                matches.Add(agent);
            }
        }

        if (matches.Count == 1)
        {
            return matches[0].Id!.Value;
        }

        var candidates = matches
            .Select(a => new CliResolutionCandidate(
                a.Id!.Value,
                a.DisplayName ?? a.Name ?? string.Empty,
                a.ParentUnit))
            .ToArray();

        throw new CliResolutionException(
            CliEntityKind.Agent,
            idOrName,
            unitContext,
            candidates);
    }

    /// <summary>
    /// Resolves a unit argument to a Guid. Same semantics as
    /// <see cref="ResolveAgentAsync"/> but constrained to the units list.
    ///
    /// <paramref name="parentContext"/> currently filters to units that
    /// match exactly that parent address. The membership graph for
    /// sub-units lives behind <c>GET /units/{parent}/members</c>; when
    /// the caller passes a parent Guid, the resolver intersects the list
    /// of all units with the parent's children.
    /// </summary>
    public async Task<Guid> ResolveUnitAsync(
        string idOrName,
        Guid? parentContext,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idOrName))
        {
            throw new CliResolutionException(
                CliEntityKind.Unit,
                idOrName ?? string.Empty,
                parentContext,
                Array.Empty<CliResolutionCandidate>());
        }

        if (TryParseGuid(idOrName, out var direct))
        {
            return direct;
        }

        // #1649: server-side `display_name` + `parent_id` filtering.
        // Mirrors the agents-side simplification — the resolver collapses
        // to a single round-trip, the server walks the parent → child
        // edge projection (#1154) for the parent constraint, and the
        // post-filter below stays as a defensive case-insensitive check
        // against pre-#1649 servers that ignore the params.
        var units = await _client.ListUnitsAsync(
            displayName: idOrName,
            parentId: parentContext,
            ct: ct);

        var matches = new List<UnitResponse>();
        foreach (var unit in units)
        {
            if (unit.Id is not Guid uid)
            {
                continue;
            }

            if (MatchesDisplayName(unit.DisplayName, idOrName)
                || MatchesDisplayName(unit.Name, idOrName))
            {
                matches.Add(unit);
            }
        }

        if (matches.Count == 1)
        {
            return matches[0].Id!.Value;
        }

        var candidates = matches
            .Select(u => new CliResolutionCandidate(
                u.Id!.Value,
                u.DisplayName ?? u.Name ?? string.Empty,
                ParentContext: null))
            .ToArray();

        throw new CliResolutionException(
            CliEntityKind.Unit,
            idOrName,
            parentContext,
            candidates);
    }

    private static bool MatchesDisplayName(string? candidate, string query)
        => !string.IsNullOrWhiteSpace(candidate)
           && string.Equals(candidate.Trim(), query.Trim(), StringComparison.OrdinalIgnoreCase);

}

/// <summary>
/// Kind of entity being resolved. Lets <see cref="CliResolutionException"/>
/// produce a kind-aware error message ("No agent found …", "No unit found
/// …") without the resolver caller having to thread the noun through.
/// </summary>
public enum CliEntityKind
{
    Agent,
    Unit,
}

/// <summary>
/// One candidate row in an n-match disambiguation list.
/// <see cref="ParentContext"/> is optional context information (e.g. the
/// agent's parent unit) so operators can tell two same-named entities
/// apart at a glance.
/// </summary>
public sealed record CliResolutionCandidate(
    Guid Id,
    string DisplayName,
    string? ParentContext);

/// <summary>
/// Raised when a <see cref="CliResolver"/> call has 0 candidates or n &gt; 1
/// candidates. The caller catches this, prints the disambiguation list,
/// and exits non-zero. The exception is intentionally CLI-internal — it
/// never escapes a command action.
/// </summary>
public sealed class CliResolutionException : Exception
{
    public CliResolutionException(
        CliEntityKind kind,
        string query,
        Guid? context,
        IReadOnlyList<CliResolutionCandidate> candidates)
        : base(BuildMessage(kind, query, context, candidates))
    {
        Kind = kind;
        Query = query;
        Context = context;
        Candidates = candidates;
    }

    public CliEntityKind Kind { get; }

    public string Query { get; }

    public Guid? Context { get; }

    public IReadOnlyList<CliResolutionCandidate> Candidates { get; }

    public bool IsAmbiguous => Candidates.Count > 1;

    private static string BuildMessage(
        CliEntityKind kind,
        string query,
        Guid? context,
        IReadOnlyList<CliResolutionCandidate> candidates)
    {
        var noun = kind switch
        {
            CliEntityKind.Agent => "agent",
            CliEntityKind.Unit => "unit",
            _ => "entity",
        };

        var contextSuffix = context is Guid g
            ? $" in unit '{Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(g)}'"
            : string.Empty;

        return candidates.Count switch
        {
            0 => $"No {noun} found matching '{query}'{contextSuffix}.",
            1 => $"Resolved {noun} '{query}'{contextSuffix} to a single match.",
            _ => $"Multiple {noun}s match '{query}'{contextSuffix}. Specify by id.",
        };
    }
}
