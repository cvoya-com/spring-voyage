// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Signals how urgently an <see cref="AmendmentPayload"/> must be honoured by
/// a live agent turn. See #142. The recipient picks a priority-appropriate
/// window to incorporate the amendment:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>
/// <term><see cref="Informational"/></term>
/// <description>Best-effort — may be deferred until the current tool call
/// or the current turn completes. The agent will always see it on the next
/// model call, never discarded silently.</description>
/// </item>
/// <item>
/// <term><see cref="MustRead"/></term>
/// <description>Must be folded into the next model call. Appropriate for
/// course-corrections that should land before the agent commits to a
/// downstream action.</description>
/// </item>
/// <item>
/// <term><see cref="StopAndWait"/></term>
/// <description>Breaks out of the current turn immediately: cancels the
/// active-work token, suspends the conversation, and flips the agent to a
/// "paused awaiting clarification" state until explicitly resumed. Use for
/// emergency course-corrections ("stop and wait for approval").</description>
/// </item>
/// </list>
/// </remarks>
public enum AmendmentPriority
{
    /// <summary>Informational amendment — deferrable to the next model call.</summary>
    Informational,

    /// <summary>Must be folded into the next model call before any tool use.</summary>
    MustRead,

    /// <summary>Break out of the current turn and wait for explicit resume.</summary>
    StopAndWait,
}