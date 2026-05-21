// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Launchers;

internal static class MessagingToolsContract
{
    /// <summary>
    /// Env var the launcher writes carrying the platform messaging tools
    /// (<c>sv.messaging.send</c> / <c>sv.messaging.broadcast</c>) for the
    /// agent / unit container. The value is a JSON-serialised
    /// <c>MessagingToolDescriptor[]</c>. Written for every agent / unit
    /// caller — the messaging surface is not gated on having members.
    /// </summary>
    public const string EnvVar = "SPRING_MESSAGING_TOOLS";
}
