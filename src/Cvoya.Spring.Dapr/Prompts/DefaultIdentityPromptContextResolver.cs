// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Prompts;

using System.Text;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// OSS-default <see cref="IIdentityPromptContextResolver"/>. Renders a
/// minimal <c>### Who you are</c> sub-section from the data
/// <see cref="IAgentDefinitionProvider"/> already serves at bundle build
/// time: kind (agent / unit, derived from the subject's address scheme),
/// address, display name, and — when present — the agent's owning unit
/// id. Authors can override by registering their own resolver before
/// <c>AddCvoyaSpringDapr()</c>.
/// </summary>
/// <remarks>
/// <para>
/// The OSS default intentionally stays storage-light: it reads the
/// agent definition only, not the unit-membership graph, expertise
/// store, or human-presence store. Richer renderings (member listings,
/// expertise breakdowns, parent-chain walks) are cloud-overlay
/// territory — register a decorating resolver before
/// <c>AddCvoyaSpringDapr()</c> to extend without forking this class.
/// </para>
/// <para>
/// A null return from <see cref="ResolveAsync"/> means "no identity to
/// surface" and the assembler omits the section entirely — applies to
/// synthetic launches that supply a subject the directory does not
/// know about, and to address schemes other than <c>agent:</c> /
/// <c>unit:</c>.
/// </para>
/// </remarks>
public class DefaultIdentityPromptContextResolver(
    IAgentDefinitionProvider agentDefinitionProvider,
    ILoggerFactory loggerFactory) : IIdentityPromptContextResolver
{
    /// <summary>
    /// The fixed heading the assembler does NOT wrap — the resolver
    /// owns its layout choices, including the heading level. Exposed
    /// as an internal constant so tests can pin against the same
    /// string the resolver emits.
    /// </summary>
    internal const string Heading = "### Who you are";

    private readonly ILogger _logger = loggerFactory.CreateLogger<DefaultIdentityPromptContextResolver>();

    /// <inheritdoc />
    public async Task<string?> ResolveAsync(
        Address subject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        // The identity section makes sense only for participant kinds
        // the agent itself can reason about. Connector or other custom
        // schemes get nothing — the assembler will omit the section.
        var kind = subject.Scheme switch
        {
            Address.AgentScheme => "agent",
            Address.UnitScheme => "unit",
            _ => null,
        };
        if (kind is null)
        {
            return null;
        }

        var subjectId = GuidFormatter.Format(subject.Id);
        var definition = await agentDefinitionProvider.GetByIdAsync(subjectId, cancellationToken);
        if (definition is null)
        {
            _logger.LogDebug(
                "Identity resolution: no definition for {Scheme}:{Id}; omitting section.",
                subject.Scheme, subjectId);
            return null;
        }

        var builder = new StringBuilder();
        builder.AppendLine(Heading);
        builder.AppendLine();

        // ADR-0053: a unit IS an agent that has members; describe the
        // kind in the platform's user-facing terms (agent / unit) so
        // the runtime can reason about itself consistently with the
        // platform introduction in the platform-instructions section.
        builder.AppendLine($"- **Kind:** {kind}");
        builder.AppendLine($"- **Address:** `{subject.Scheme}:{subjectId}`");

        if (!string.IsNullOrWhiteSpace(definition.Name))
        {
            builder.AppendLine($"- **Display name:** {definition.Name}");
        }

        // ADR-0036 / DbAgentDefinitionProvider: UnitId is the canonical
        // Guid wire form of the agent's primary owning unit, or null
        // when the agent has no membership. For a unit-as-agent
        // (ADR-0039) the projection still sets UnitId to the unit's own
        // id — render only when distinct from the subject to avoid
        // saying "Parent unit: yourself".
        if (!string.IsNullOrWhiteSpace(definition.UnitId)
            && !string.Equals(definition.UnitId, subjectId, StringComparison.OrdinalIgnoreCase))
        {
            builder.AppendLine($"- **Parent unit:** `unit:{definition.UnitId}`");
        }

        // #2741: declared role and expertise are properties of the
        // agent / unit definition itself — the provider read them off
        // the same definition JSON above and there is no extra lookup
        // to defer. Render them inline so a fresh runtime does not pay
        // a per-turn sv.directory.lookup round-trip just to recover its
        // own static metadata. Live / membership-shaped data (siblings,
        // members) genuinely changes between turns and stays behind the
        // sv.directory.list pointer below.
        if (!string.IsNullOrWhiteSpace(definition.Role))
        {
            builder.AppendLine($"- **Role:** {definition.Role}");
        }

        if (definition.Expertise is { Count: > 0 } expertise)
        {
            builder.AppendLine("- **Expertise:**");
            foreach (var domain in expertise)
            {
                builder.AppendLine($"  - {FormatExpertiseDomain(domain)}");
            }
        }

        // The OSS default surfaces the always-available identity fields
        // (kind, address, display name, parent unit, role, expertise)
        // and points the runtime at the directory tools only for
        // live / membership-shaped data — sibling enumeration, peer
        // discovery, and (for units) member listings — which genuinely
        // changes between turns. The catalog in the platform
        // instructions names these tools; this line is a use-site
        // reminder.
        builder.AppendLine();
        builder.AppendLine("Use `sv.directory.list` to enumerate your members, siblings, or peers — the platform serves the live view rather than freezing it in this snapshot.");

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Renders one expertise domain as a single inline bullet — the
    /// domain name, the optional level in parentheses, and the
    /// description on the same line when present. Mirrors the
    /// summary shape <c>sv.directory.lookup</c>'s caller would compose
    /// from the structured response, so the inline render and the
    /// tool's response stay readable in the same way.
    /// </summary>
    private static string FormatExpertiseDomain(ExpertiseDomain domain)
    {
        var name = string.IsNullOrWhiteSpace(domain.Name) ? "(unnamed)" : domain.Name;
        var head = domain.Level is { } level
            ? $"{name} ({level.ToString().ToLowerInvariant()})"
            : name;
        return string.IsNullOrWhiteSpace(domain.Description)
            ? head
            : $"{head} — {domain.Description}";
    }
}
