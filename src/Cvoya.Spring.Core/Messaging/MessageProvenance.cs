// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Where a <see cref="Message"/> originated, used to classify the cost of the
/// turn it triggers (issue #3075). Carried on the message because it is the
/// only signal that survives the router / mailbox boundary to the dispatching
/// actor — the same way connector origin survives as the <c>connector://</c>
/// <see cref="Message.From"/> scheme.
/// </summary>
public enum MessageProvenance
{
    /// <summary>
    /// A directly-sent message: a human, another agent / unit, a connector
    /// event, or a runtime's own <c>sv.messaging.*</c> tool call. The turn it
    /// triggers is normal work (<see cref="Costs.CostSource.Work"/>).
    /// </summary>
    Direct = 0,

    /// <summary>
    /// A message the agent's own initiative (Tier-2 reflection) loop produced
    /// — a reflection action translated into a message. When such a message is
    /// addressed back to the originating agent (a self-targeted reflection
    /// action), the turn it triggers is the agent acting on its own initiative
    /// and bills as <see cref="Costs.CostSource.Initiative"/>. A reflection
    /// message addressed to a <em>different</em> agent still triggers normal
    /// work on the recipient — the recipient is responding, not self-initiating
    /// — so the dispatch coordinator only treats a self-addressed initiative
    /// message as initiative cost.
    /// </summary>
    Initiative = 1,
}
