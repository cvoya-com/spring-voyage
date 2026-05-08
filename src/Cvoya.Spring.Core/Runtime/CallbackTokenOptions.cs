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
    /// Token lifetime. Defaults to five minutes — long enough to absorb
    /// runtime-launch latency on a cold container start, short enough that
    /// a leaked token rotates out before it can be replayed across
    /// invocations.
    /// </summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Clock-skew tolerance applied during validation. Defaults to thirty
    /// seconds — large enough to absorb the host-vs-container skew the
    /// dispatcher routinely sees in OSS deploys, small enough that a
    /// minute-and-a-half-old token still rejects.
    /// </summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);
}