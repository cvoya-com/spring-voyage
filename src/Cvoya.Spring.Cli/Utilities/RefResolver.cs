// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Utilities;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// CLI-side resolvers for the <c>&lt;human-ref&gt;</c> and
/// <c>&lt;tenant-user-ref&gt;</c> ref shapes ADR-0062 § 6 specifies for
/// the <c>spring message send --as</c>, <c>spring user identity
/// set-primary</c>, <c>spring unit members humans add --as</c>, and
/// <c>spring package install --as-human</c> verbs (#2827, #2822).
/// </summary>
/// <remarks>
/// <para>
/// <b><c>&lt;human-ref&gt;</c>.</b> Accepts a Guid (dashed or no-dash) or
/// a display-name string matched case-insensitively against the calling
/// caller's bound-Hat set via
/// <c>GET /api/v1/tenant/users/me/humans</c>. The bound-set query is the
/// same surface the portal's <c>HumanFromSelector</c> reads, so the CLI
/// and the portal resolve identical inputs identically.
/// </para>
/// <para>
/// <b><c>&lt;tenant-user-ref&gt;</c>.</b> Accepts a Guid, the literal
/// <c>me</c> (which resolves to <see cref="OssTenantUserIds.Operator"/>
/// in OSS — cloud overlays plug in a <c>/me</c> equivalent through the
/// same accessor that backs the API), or a non-Guid non-<c>me</c>
/// string interpreted as an OAuth subject and resolved via
/// <c>GET /api/v1/tenant/users?authSubject=&lt;...&gt;</c>.
/// </para>
/// </remarks>
public static class RefResolver
{
    /// <summary>
    /// Resolves a <c>&lt;human-ref&gt;</c> to a Hat <see cref="Guid"/>.
    /// Bare Guid passes through untouched (no round-trip); non-Guid
    /// inputs are matched case-insensitively against the calling
    /// caller's bound-Hat set. Returns the resolved id; throws
    /// <see cref="CliRefResolutionException"/> with operator-friendly
    /// text when zero or multiple bound Hats match the supplied name.
    /// </summary>
    /// <param name="client">SpringApiClient instance for the round-trip.</param>
    /// <param name="input">The raw <c>--as</c> / argument value typed by the operator.</param>
    /// <param name="flagDescription">
    /// Operator-visible flag name (e.g. <c>--as</c>, <c>human-ref</c>)
    /// used to phrase the error message. Renders verbatim in the
    /// thrown error text.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<Guid> ResolveHumanRefAsync(
        SpringApiClient client,
        string input,
        string flagDescription,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new CliRefResolutionException(
                $"{flagDescription} is required: pass a Hat UUID or display name.");
        }

        // Bare Guid → use directly. Avoids the round-trip on the most
        // common scripted-input shape and keeps existing callers fast.
        if (Guid.TryParse(input, out var parsed) && parsed != Guid.Empty)
        {
            return parsed;
        }

        // Display-name lookup. The bound-set is small (one operator =
        // a handful of Hats) so a server-side scan is cheap; no client
        // cache needed for v0.1.
        IReadOnlyList<global::Cvoya.Spring.Cli.Generated.Models.CallerHumanResponse> hats;
        try
        {
            hats = await client.ListCallerHumansAsync(ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw new CliRefResolutionException(
                $"Could not resolve {flagDescription} '{input}': failed to load your bound Hats — " +
                ProblemDetailsTranslator.Format(ex));
        }

        var matches = hats
            .Where(h => !string.IsNullOrWhiteSpace(h.DisplayName)
                && string.Equals(h.DisplayName, input, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            throw new CliRefResolutionException(
                $"No bound Hat matches {flagDescription} '{input}'. " +
                $"Run `spring user identity list` to see your bound Hats, " +
                $"or pass the Hat UUID directly.");
        }
        if (matches.Count > 1)
        {
            var ids = string.Join(", ", matches
                .Select(h => h.HumanId is { } id ? id.ToString("N") : "?"));
            throw new CliRefResolutionException(
                $"{flagDescription} '{input}' matches more than one Hat ({matches.Count} Hats: " +
                $"{ids}). Re-run with the specific Hat UUID to disambiguate.");
        }

        var only = matches[0];
        if (only.HumanId is not { } resolvedId || resolvedId == Guid.Empty)
        {
            throw new CliRefResolutionException(
                $"Server returned an empty Hat id for {flagDescription} '{input}'.");
        }
        return resolvedId;
    }

    /// <summary>
    /// Resolves a <c>&lt;tenant-user-ref&gt;</c> per ADR-0062 § 6.
    /// Accepts a Guid (dashed or no-dash), the literal <c>me</c>
    /// (→ <see cref="OssTenantUserIds.Operator"/> on OSS; cloud
    /// overlays plug in their own /me-equivalent), or an OAuth subject
    /// resolved through the server. Returns the resolved
    /// <see cref="Guid"/> or throws
    /// <see cref="CliRefResolutionException"/> with operator-friendly
    /// text for unresolved inputs.
    /// </summary>
    /// <param name="client">SpringApiClient instance for the round-trip.</param>
    /// <param name="input">The raw value typed by the operator.</param>
    /// <param name="flagDescription">
    /// Operator-visible flag name (e.g. <c>--as</c>) for the error
    /// message.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<Guid> ResolveTenantUserRefAsync(
        SpringApiClient client,
        string input,
        string flagDescription,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new CliRefResolutionException(
                $"{flagDescription} is required: pass a TenantUser UUID, 'me', or an OAuth subject.");
        }

        if (string.Equals(input, "me", StringComparison.OrdinalIgnoreCase))
        {
            // OSS deployments pin the operator to a deterministic UUID;
            // the cloud overlay would surface a future /me-equivalent
            // through the same accessor pathway. v0.1 OSS: literal
            // operator UUID. See UnitMembersCommand.ResolveTenantUserRef
            // (the sync sibling) for the equivalent rationale.
            return OssTenantUserIds.Operator;
        }

        if (Guid.TryParse(input, out var parsed) && parsed != Guid.Empty)
        {
            return parsed;
        }

        // OAuth-subject lookup. The server's
        // GET /api/v1/tenant/users?authSubject=<...> endpoint returns
        // 404 when no row matches; the CLI surface translates that
        // into a precise CliRefResolutionException so the operator
        // sees a one-line "no such user" rather than a stack trace.
        global::Cvoya.Spring.Cli.Generated.Models.TenantUserResponse? row;
        try
        {
            row = await client.FindTenantUserByAuthSubjectAsync(input, ct);
        }
        catch (Microsoft.Kiota.Abstractions.ApiException ex)
        {
            throw new CliRefResolutionException(
                $"Could not resolve {flagDescription} '{input}': failed to look up TenantUser by auth subject — " +
                ProblemDetailsTranslator.Format(ex));
        }

        if (row is null || row.Id is not { } id || id == Guid.Empty)
        {
            throw new CliRefResolutionException(
                $"No TenantUser in the current tenant has auth subject '{input}'. " +
                "Pass a TenantUser UUID, 'me', or the OAuth subject of an existing user.");
        }
        return id;
    }
}

/// <summary>
/// Thrown by <see cref="RefResolver"/> when a <c>&lt;human-ref&gt;</c> or
/// <c>&lt;tenant-user-ref&gt;</c> argument cannot be resolved. CLI verbs
/// catch this exception, write its <see cref="Exception.Message"/> to
/// stderr, and exit with code 1 — same shape as
/// <see cref="CliResolutionException"/>'s render path but with simpler
/// message content (no SuggestedAlternatives surface needed for the
/// ADR-0062 ref shapes).
/// </summary>
public sealed class CliRefResolutionException : Exception
{
    /// <summary>Creates a new <see cref="CliRefResolutionException"/>.</summary>
    public CliRefResolutionException(string message) : base(message)
    {
    }
}
