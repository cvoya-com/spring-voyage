// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Catalog;

using System.Text.Json.Serialization;

/// <summary>
/// How the platform-assembled system prompt combines with the runtime's own
/// default system prompt (e.g. Claude Code's coding-assistant baseline). Per
/// the sub-issue chain of #2667 — non-coding runtimes (routers, PMs,
/// analysts) want to opt out of the runtime's default prompt entirely so
/// the assembled prompt is the sole instructions surface.
/// </summary>
/// <remarks>
/// <para>
/// The mode is declared on both agent and unit <c>execution:</c> blocks
/// and resolved at dispatch time with the standard
/// <i>agent → unit → default (<see cref="Append"/>)</i> cascade. Launcher
/// consumption lands in a separate sub-issue; the field is plumbed through
/// the cascade and surfaces on
/// <see cref="Cvoya.Spring.Core.Execution.AgentLaunchContext.SystemPromptMode"/>
/// for that next wave.
/// </para>
/// <para>
/// Serialised to JSON as lowercase strings via
/// <see cref="JsonStringEnumMemberNameAttribute"/> so the wire form matches
/// the YAML and OpenAPI literals.
/// </para>
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SystemPromptMode>))]
public enum SystemPromptMode
{
    /// <summary>
    /// Append the platform-assembled prompt to the runtime's own default
    /// system prompt. The default — preserves the runtime's coding-assistant
    /// guidance for engineer-shaped agents.
    /// </summary>
    [JsonStringEnumMemberName("append")]
    Append = 0,

    /// <summary>
    /// Replace the runtime's default system prompt with the platform-assembled
    /// prompt. Non-coding agents (routers, PMs, analysts) opt in here so the
    /// runtime's coding-assistant baseline does not shape responses.
    /// </summary>
    [JsonStringEnumMemberName("replace")]
    Replace = 1,
}
