// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Surfaces the platform messaging tools an agent or unit may invoke to
/// deliver messages to addressable targets within a given thread.
///
/// The platform delivers messages; it does not orchestrate (ADR-0048 /
/// ADR-0049). The messaging surface is the two delivery verbs
/// (<c>sv.messaging.send</c>, <c>sv.messaging.broadcast</c>) — every
/// agent and unit caller gets them, independent of whether it has members.
/// </summary>
/// <remarks>
/// <para>
/// Implementations describe the tools available — not invoke them. The
/// returned <see cref="MessagingToolDescriptor"/> array carries the
/// canonical <see cref="MessagingToolName"/> and the JSON Schemas the
/// caller advertises to the agent runtime. An empty array is a valid
/// result for callers that are not messaging callers (e.g.
/// <c>human://</c> / <c>connector://</c>).
/// </para>
/// <para>
/// The <paramref name="threadId"/> parameter is part of the contract so a
/// provider can scope tool availability to a particular conversation.
/// Providers that do not need thread-scoping ignore the parameter.
/// </para>
/// </remarks>
public interface IMessagingToolProvider
{
    /// <summary>
    /// Returns the messaging tools available to <paramref name="agent"/>
    /// within thread <paramref name="threadId"/>. Returns an empty array
    /// when the addressed entity is not a messaging caller.
    /// </summary>
    /// <param name="agent">The address of the agent or unit whose messaging tools are being requested.</param>
    /// <param name="threadId">The conversation thread the tools are scoped to.</param>
    MessagingToolDescriptor[] GetMessagingTools(Address agent, Guid threadId);
}
