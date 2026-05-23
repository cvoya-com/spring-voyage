// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Sample.ToolsAgent;

using System.Text.Json;

using Cvoya.Spring.AgentSdk;
using Cvoya.Spring.Core.Skills;

/// <summary>
/// Sample tool registrations. The two <c>acme.*</c> tools exist so the
/// platform-side introspection logic (#2336 / Sub C of #2332) has a
/// concrete image-tier surface to deploy against in tests.
/// </summary>
public static class AcmeTools
{
    /// <summary>Registers the sample tools on <paramref name="registry"/>.</summary>
    public static void RegisterAcmeTools(this IToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(
            new ToolDefinition(
                Name: "acme.echo",
                Description: "Echoes the input string back.",
                InputSchema: ParseSchema(EchoInputSchema),
                Category: string.Empty),
            handler: static (args, _) => Task.FromResult(args));

        registry.Register(
            new ToolDefinition(
                Name: "acme.timestamp",
                Description: "Returns the current UTC timestamp.",
                InputSchema: ParseSchema(TimestampInputSchema),
                Category: string.Empty),
            handler: static (_, _) =>
            {
                var element = JsonSerializer.SerializeToElement(new
                {
                    utc = DateTimeOffset.UtcNow.ToString("O"),
                });
                return Task.FromResult(element);
            });
    }

    private const string EchoInputSchema =
        """
        {
          "type": "object",
          "required": ["value"],
          "properties": {
            "value": { "type": "string" }
          }
        }
        """;

    private const string TimestampInputSchema =
        """
        {
          "type": "object",
          "properties": {}
        }
        """;

    private static JsonElement ParseSchema(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }
}
