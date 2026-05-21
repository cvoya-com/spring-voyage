// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Runtime;

/// <summary>
/// Options for callback-token issuance and validation. Bound from the
/// <c>Dispatcher:CallbackToken</c> configuration section.
/// </summary>
/// <remarks>
/// Per-invocation callback tokens (ADR-0039 D12) are short-lived by design:
/// they exist only for the lifetime of one runtime invocation. Operators
/// generally have no reason to lengthen the lifetime. The setting is
/// exposed primarily so deployment-side tests can clamp it to a few
/// seconds when exercising the expiry path against the live host.
/// </remarks>
public class CallbackTokenOptions
{
    /// <summary>Configuration section name (relative to the Dispatcher root).</summary>
    public const string SectionName = "Dispatcher:CallbackToken";

    /// <summary>Issuer claim written into every token.</summary>
    public const string Issuer = "spring-voyage-dispatcher";

    /// <summary>Audience claim written into every token.</summary>
    public const string Audience = "spring-voyage-runtime";

    /// <summary>
    /// Token lifetime. Minted once at the start of a runtime turn and not
    /// renewed within that turn, so it must outlast the whole turn.
    /// <para>
    /// INTERIM (#2592): raised to 60 minutes. The original 5-minute default
    /// was shorter than a realistic engineer-agent turn (clone, implement,
    /// build, test, open PR) — the token expired mid-turn and every
    /// subsequent <c>sv.messaging.*</c> call failed with "token rejected:
    /// Expired". 60 minutes covers a typical turn while still rotating out
    /// of a leaked-token window. The proper fix is in-turn renewal so the
    /// token survives an arbitrarily long turn without a wide static
    /// window; this default reverts to a short lifetime when #2593 lands.
    /// </para>
    /// </summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(60);

    /// <summary>
    /// Clock-skew tolerance applied during validation. Defaults to thirty
    /// seconds — large enough to absorb the host-vs-container skew the
    /// dispatcher routinely sees in OSS deploys, small enough that a
    /// minute-and-a-half-old token still rejects.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}
