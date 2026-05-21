// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using System.Reflection;
using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;

/// <summary>
/// Default <see cref="IMessagingToolProvider"/> backing the v0.1 platform
/// messaging-tool surface (ADR-0048 / ADR-0049). Returns the closed two-tool
/// set unconditionally for every <c>agent://</c> or <c>unit://</c> address;
/// other schemes return an empty array.
/// </summary>
/// <remarks>
/// <para>
/// The messaging surface is the two delivery verbs
/// (<c>sv.messaging.send</c>, <c>sv.messaging.broadcast</c>) only —
/// discovery, inspection, and status queries live on the
/// <c>sv.directory.*</c> tool surface. The platform delivers messages; it
/// does not orchestrate. The tools are available to every agent and unit
/// caller, independent of whether the caller has members.
/// </para>
/// <para>
/// The descriptors are static — built once from embedded JSON schema
/// resources at startup — so per-call work is O(1). The schemas live next
/// to this file under
/// <c>Orchestration/Resources/&lt;tool-name&gt;.&lt;input|output&gt;.schema.json</c>
/// and are wired in as an <c>&lt;EmbeddedResource&gt;</c> glob in
/// <c>Cvoya.Spring.Dapr.csproj</c>.
/// </para>
/// </remarks>
public class MessagingToolProvider : IMessagingToolProvider
{
    private readonly MessagingToolDescriptor[] _toolset = LoadStaticToolset();

    /// <inheritdoc />
    public MessagingToolDescriptor[] GetMessagingTools(Address agent, Guid threadId)
    {
        ArgumentNullException.ThrowIfNull(agent);

        // Schemes outside agent:// and unit:// (e.g. human://, connector://)
        // are not messaging callers — return empty rather than the closed
        // two-tool set.
        if (!string.Equals(agent.Scheme, Address.AgentScheme, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(agent.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<MessagingToolDescriptor>();
        }

        return _toolset;
    }

    /// <summary>
    /// Loads the two messaging-tool descriptors once from embedded JSON
    /// schema resources. The order matches the <see cref="MessagingToolName"/>
    /// declaration so the toolset reads deterministically in logs and
    /// tool-call catalogues.
    /// </summary>
    private static MessagingToolDescriptor[] LoadStaticToolset()
    {
        var assembly = typeof(MessagingToolProvider).Assembly;

        return new[]
        {
            BuildDescriptor(assembly, MessagingToolName.Send, "sv.messaging.send"),
            BuildDescriptor(assembly, MessagingToolName.Broadcast, "sv.messaging.broadcast"),
        };
    }

    private static MessagingToolDescriptor BuildDescriptor(
        Assembly assembly,
        MessagingToolName name,
        string wireName)
    {
        var input = LoadSchema(assembly, $"{wireName}.input.schema.json");
        var output = LoadSchema(assembly, $"{wireName}.output.schema.json");
        return new MessagingToolDescriptor(name, input, output);
    }

    private static JsonElement LoadSchema(Assembly assembly, string fileName)
    {
        var resourceName = $"Cvoya.Spring.Dapr.Orchestration.Resources.{fileName}";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"Embedded messaging-tool schema '{resourceName}' was not found. " +
                "Verify the EmbeddedResource glob in Cvoya.Spring.Dapr.csproj.");

        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}
