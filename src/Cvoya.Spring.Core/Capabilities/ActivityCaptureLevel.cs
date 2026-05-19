// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Tenant-level knob controlling how much of every captured activity
/// event payload (prompt body, tool arguments, LLM input/output text)
/// is persisted. Applied <strong>server-side at ingest</strong> — the
/// runtime always emits full payloads; the ingest controller truncates
/// or drops based on this setting (issue #2492).
/// </summary>
public enum ActivityCaptureLevel
{
    /// <summary>
    /// Capture nothing. Runtime activity events are dropped before
    /// reaching the bus, persistence, or any subscriber. The runtime
    /// status surface (issue #2491) continues to work — only the
    /// captured-payload event types defined in #2492 are filtered out.
    /// </summary>
    Off,

    /// <summary>
    /// Capture identity + summary metadata for every event. Free-text
    /// payloads (assembled prompts, tool args, LLM input/output, log
    /// bodies) are truncated to first / last N characters with a
    /// <c>truncated: true</c> marker on the persisted details payload.
    /// </summary>
    Summary,

    /// <summary>
    /// Capture everything — the OSS default. Prompts, tool args, tool
    /// results, and LLM input/output are persisted in full. Redaction
    /// (see <c>ActivityRedactor</c>) still runs on credential-shaped
    /// fields regardless of level.
    /// </summary>
    Full,
}
