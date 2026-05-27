// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Outbound;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Resolves an SV-side participant address to the
/// <c>(username, icon_url)</c> pair the bot uses when posting on
/// behalf of that participant via <c>chat.postMessage</c> with the
/// persona-override flags (ADR-0061 §3 — "Persona overrides for
/// non-bound participants"). Requires the
/// <c>chat:write.customize</c> OAuth scope.
///
/// <para>
/// Address schemes:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <c>agent:&lt;guid&gt;</c> → the agent's display name from
///     <c>AgentDefinitions.DisplayName</c>; <c>icon_url</c> is the
///     agent's avatar URL when one is configured, otherwise a
///     deterministic fallback Gravatar-like placeholder derived from
///     the agent's id.
///   </description></item>
///   <item><description>
///     <c>unit:&lt;guid&gt;</c> → the unit's display name; icon as
///     for agents.
///   </description></item>
///   <item><description>
///     <c>human:&lt;guid&gt;</c> → the human's display name; icon
///     resolution is currently the placeholder. Humans posting
///     themselves use their native Slack identity — the persona
///     override only applies when the bot is acting on behalf of a
///     non-bound SV human.
///   </description></item>
///   <item><description>
///     <c>connector:&lt;guid&gt;</c> → connector display name + icon.
///     Inbound webhook translations (#2818 is for outbound but this
///     covers the synthetic "from connector" address that may surface
///     when a connector-originated message is replayed into Slack).
///   </description></item>
/// </list>
/// </summary>
public interface ISlackPersonaBuilder
{
    /// <summary>
    /// Resolves <paramref name="participant"/> to a persona descriptor.
    /// Never returns <c>null</c>; resolution failures fall back to a
    /// per-scheme generic ("an agent", "a unit", ...) matching the
    /// <see cref="Cvoya.Spring.Core.Security.IParticipantDisplayNameResolver"/>
    /// fallback rule.
    /// </summary>
    Task<SlackPersona> ResolveAsync(Address participant, CancellationToken cancellationToken = default);
}

/// <summary>
/// Persona descriptor passed verbatim to <c>chat.postMessage</c>'s
/// <c>username</c> and <c>icon_url</c> fields.
/// </summary>
/// <param name="Username">Display name surfaced as the message author.</param>
/// <param name="IconUrl">Avatar URL surfaced beside the message.</param>
public sealed record SlackPersona(string Username, string IconUrl);
