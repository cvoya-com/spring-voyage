// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Utilities;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// CLI-side resolvers for the <c>&lt;human-ref&gt;</c> and
/// <c>&lt;tenant-user-ref&gt;</c> ref shapes ADR-0062 § 6 specifies for
/// the <c>spring message send --as</c>, <c>spring user identity
/// set-primary</c>, <c>spring unit members humans add --as</c>, and
/// <c>spring package install --as-human</c> verbs (#2827, #2822, #2829).
/// </summary>
/// <remarks>
/// <para>
/// <b><c>&lt;human-ref&gt;</c>.</b> Accepts a Guid (dashed or no-dash) or
/// a display-name / disambiguated-label string matched case-
/// insensitively against the calling caller's bound-Hat set via
/// <c>GET /api/v1/tenant/users/me/humans</c>. The bound-set query is the
/// same surface the portal's <c>HumanFromSelector</c> reads, so the CLI
/// and the portal resolve identical inputs identically.
/// </para>
/// <para>
/// <b>Ambiguity handling (#2829).</b> When a non-Guid input matches
/// multiple Hats the resolver branches on stdin TTY-detection via
/// <see cref="Console.IsInputRedirected"/>: a true TTY (stdin not
/// redirected) prints a numbered list using the server's
/// <c>disambiguatedLabel</c> values and prompts the operator to pick
/// one. A redirected stdin (CI, piped input, scripted invocation) falls
/// back to a structured error listing the candidate disambiguated
/// labels so the operator can re-run with the unambiguous form.
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
    /// Resolves a <c>&lt;human-ref&gt;</c> to a Hat <see cref="Guid"/>
    /// using <see cref="Console.IsInputRedirected"/> for TTY detection
    /// (the production path). Tests inject a fake stdin/stdout via the
    /// <see cref="ResolveHumanRefAsync(SpringApiClient, string, string, TextReader?, TextWriter?, bool, CancellationToken)"/>
    /// overload.
    /// </summary>
    public static Task<Guid> ResolveHumanRefAsync(
        SpringApiClient client,
        string input,
        string flagDescription,
        CancellationToken ct = default) =>
        ResolveHumanRefAsync(
            client,
            input,
            flagDescription,
            stdin: null,
            stdout: null,
            isInputRedirected: Console.IsInputRedirected,
            ct);

    /// <summary>
    /// Resolves a <c>&lt;human-ref&gt;</c> to a Hat <see cref="Guid"/>.
    /// Bare Guid passes through untouched (no round-trip); non-Guid
    /// inputs are matched case-insensitively against the calling
    /// caller's bound-Hat set — first by raw <c>displayName</c>, then
    /// by server-supplied <c>disambiguatedLabel</c> (#2829). Returns
    /// the resolved id; throws <see cref="CliRefResolutionException"/>
    /// with operator-friendly text when zero candidates match. Multiple
    /// matches branch on the TTY flag: an interactive numbered prompt
    /// on a real terminal, a structured error otherwise.
    /// </summary>
    /// <param name="client">SpringApiClient instance for the round-trip.</param>
    /// <param name="input">The raw <c>--as</c> / argument value typed by the operator.</param>
    /// <param name="flagDescription">
    /// Operator-visible flag name (e.g. <c>--as</c>, <c>human-ref</c>)
    /// used to phrase the error message. Renders verbatim in the
    /// thrown error text.
    /// </param>
    /// <param name="stdin">
    /// Stdin reader for the interactive prompt. <c>null</c> defaults to
    /// <see cref="Console.In"/>.
    /// </param>
    /// <param name="stdout">
    /// Stdout writer for the interactive prompt. <c>null</c> defaults
    /// to <see cref="Console.Out"/>.
    /// </param>
    /// <param name="isInputRedirected">
    /// TTY-detection flag. <c>true</c> means stdin is redirected (CI,
    /// piped input, scripted invocation) → fall back to the structured
    /// error. <c>false</c> means a real TTY → render the interactive
    /// prompt. The production caller passes
    /// <see cref="Console.IsInputRedirected"/>; tests inject the value
    /// they want.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<Guid> ResolveHumanRefAsync(
        SpringApiClient client,
        string input,
        string flagDescription,
        TextReader? stdin,
        TextWriter? stdout,
        bool isInputRedirected,
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

        // Display-name / disambiguated-label lookup. The bound-set is
        // small (one operator = a handful of Hats) so a server-side
        // scan is cheap; no client cache needed for v0.1.
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

        // #2829: match against both raw display name AND server-
        // supplied disambiguated label in one pass. An operator who
        // saw "Bob — designer" in the portal / CLI prompt can type
        // `--as "Bob — designer"` and resolve in one shot.
        var matches = hats
            .Where(h => Matches(h, input))
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
            return await ResolveAmbiguityAsync(
                matches,
                input,
                flagDescription,
                stdin ?? Console.In,
                stdout ?? Console.Out,
                isInputRedirected,
                ct);
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
    /// Tests whether a Hat row matches the operator-typed input — exact
    /// case-insensitive on display name OR disambiguated label. The
    /// disambiguated label often subsumes the display name (e.g.
    /// <c>"Bob"</c> when uncolliding, <c>"Bob — designer"</c> when
    /// colliding) so matching both fields covers every shape the
    /// portal renders.
    /// </summary>
    private static bool Matches(
        global::Cvoya.Spring.Cli.Generated.Models.CallerHumanResponse hat,
        string input)
    {
        if (!string.IsNullOrWhiteSpace(hat.DisplayName)
            && string.Equals(hat.DisplayName, input, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (!string.IsNullOrWhiteSpace(hat.DisambiguatedLabel)
            && string.Equals(hat.DisambiguatedLabel, input, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return false;
    }

    /// <summary>
    /// Ambiguity branch. On a TTY (stdin not redirected) renders a
    /// numbered list using the server's <c>disambiguatedLabel</c>
    /// values and prompts the operator to pick one (1–N). Invalid
    /// input re-prompts. EOF (Ctrl+D / piped stdin closed) aborts with
    /// a non-zero exit via <see cref="CliRefResolutionException"/>. On
    /// a non-TTY (stdin redirected) emits a structured error listing
    /// the candidate disambiguated labels so the operator can re-run
    /// with the unambiguous form.
    /// </summary>
    private static Task<Guid> ResolveAmbiguityAsync(
        IReadOnlyList<global::Cvoya.Spring.Cli.Generated.Models.CallerHumanResponse> matches,
        string input,
        string flagDescription,
        TextReader stdin,
        TextWriter stdout,
        bool isInputRedirected,
        CancellationToken ct)
    {
        if (isInputRedirected)
        {
            var alternatives = string.Join(
                Environment.NewLine,
                matches.Select(h => $"  {flagDescription} \"{h.DisambiguatedLabel ?? h.DisplayName}\""));
            throw new CliRefResolutionException(
                $"{flagDescription} '{input}' matched multiple Hats; re-run with one of:" +
                Environment.NewLine + alternatives);
        }

        // Interactive numbered prompt. Stable count (N is bound by the
        // result set, which is "the caller's bound Hats sharing the
        // same name") so a single int parse is enough; we re-prompt on
        // invalid input rather than fail-fast.
        stdout.WriteLine($"which \"{input}\"?");
        for (var i = 0; i < matches.Count; i++)
        {
            stdout.WriteLine($"  {i + 1}. {matches[i].DisambiguatedLabel ?? matches[i].DisplayName}");
        }

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            stdout.Write($"pick one [1-{matches.Count}]: ");
            stdout.Flush();

            var line = stdin.ReadLine();
            if (line is null)
            {
                // EOF on stdin — operator pressed ^D or the input stream
                // closed. Abort with a non-zero exit so scripts don't
                // silently pick a Hat.
                throw new CliRefResolutionException(
                    $"Aborted: {flagDescription} '{input}' is ambiguous and no choice was made.");
            }

            var trimmed = line.Trim();
            if (int.TryParse(trimmed, out var choice)
                && choice >= 1
                && choice <= matches.Count)
            {
                var pick = matches[choice - 1];
                if (pick.HumanId is not { } pickedId || pickedId == Guid.Empty)
                {
                    throw new CliRefResolutionException(
                        $"Server returned an empty Hat id for the picked entry.");
                }
                return Task.FromResult(pickedId);
            }

            stdout.WriteLine($"  invalid choice '{trimmed}', please pick a number between 1 and {matches.Count}.");
        }
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
