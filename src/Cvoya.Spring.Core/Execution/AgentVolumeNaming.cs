// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Stable, collision-free volume-name derivation for per-agent workspace
/// volumes (D3c — ADR-0029). The convention guarantees:
/// <list type="bullet">
///   <item>One volume per agent identity — same name across restarts.</item>
///   <item>No cross-agent collisions — different agents produce different names.</item>
///   <item>Runtime-safe identifiers — only lowercase alphanumeric and hyphens,
///         which both Podman and Docker accept as named-volume identifiers.</item>
///   <item>Bounded length — Podman and Docker cap volume names at 255 characters;
///         the scheme stays well below that.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Convention: <c>spring-ws-{sanitised-agentId}</c>.
/// </para>
/// <para>
/// The <c>spring-ws-</c> prefix marks the volume as platform-owned (workspace)
/// and makes it visually distinct from other Podman / Docker volumes (e.g.
/// bind-mounted config volumes, Dapr state-store volumes) in <c>podman volume ls</c>
/// output.
/// </para>
/// <para>
/// The <c>agentId</c> is already unique within the platform (it encodes the
/// tenant / unit / agent path). Non-alphanumeric characters (slashes, colons,
/// dots, underscores) are replaced with hyphens and the result is lowercased.
/// Consecutive hyphens are collapsed to a single hyphen; leading and trailing
/// hyphens are removed. The final identifier is truncated at 220 characters to
/// leave headroom for the prefix.
/// </para>
/// </remarks>
public static class AgentVolumeNaming
{
    /// <summary>Prefix applied to every platform-managed workspace volume.</summary>
    public const string Prefix = "spring-ws-";

    /// <summary>
    /// Maximum number of characters taken from the sanitised agent id (beyond
    /// the prefix). Keeps the total well under Podman / Docker's 255-char cap.
    /// </summary>
    private const int MaxAgentIdSegment = 220;

    /// <summary>
    /// Derives the stable Podman named-volume identifier for an agent's
    /// workspace volume.
    /// </summary>
    /// <param name="agentId">
    /// The agent's stable platform identifier (e.g.
    /// <c>tenants/acme/units/eng/agents/backend-engineer</c> or the short-form
    /// id used internally by the registry).
    /// </param>
    /// <returns>
    /// A volume name that is safe to pass to <c>podman volume create</c> and
    /// <c>podman run -v &lt;name&gt;:&lt;path&gt;</c>.
    /// </returns>
    public static string ForAgent(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        // Replace every non-alphanumeric character with a hyphen, lowercase,
        // collapse consecutive hyphens, and trim leading/trailing hyphens.
        var sanitised = string.Concat(agentId.Select(c =>
            char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-'));

        // Collapse consecutive hyphens and trim boundary hyphens.
        while (sanitised.Contains("--", StringComparison.Ordinal))
        {
            sanitised = sanitised.Replace("--", "-", StringComparison.Ordinal);
        }
        sanitised = sanitised.Trim('-');

        if (sanitised.Length > MaxAgentIdSegment)
        {
            sanitised = sanitised[..MaxAgentIdSegment];
            sanitised = sanitised.TrimEnd('-');
        }

        return Prefix + sanitised;
    }
}
