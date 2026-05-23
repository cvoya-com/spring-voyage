// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Prompts;

using System.Text;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;

/// <summary>
/// Assembles the per-agent system prompt by composing three layers:
/// platform instructions (Layer 1), unit context (Layer 2), and agent
/// instructions (Layer 4). Thread history was previously Layer 3 and is
/// now delivered by each runtime's session-resume mechanism rather than
/// duplicated in the assembled prompt — see <see cref="PromptAssemblyContext"/>
/// remarks. Stateless and safe to share across concurrent actors — all
/// per-agent state is passed through <see cref="AssembleAsync"/>.
/// </summary>
public class PromptAssembler(
    IPlatformPromptProvider platformPromptProvider,
    UnitContextBuilder unitContextBuilder,
    AgentInstructionsBuilder agentInstructionsBuilder,
    ILoggerFactory loggerFactory) : IPromptAssembler
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<PromptAssembler>();

    /// <summary>
    /// Heading used for the platform-injected connector-context
    /// subsection. Exposed as a constant so tests and downstream
    /// docs can pin the exact rendered text.
    /// </summary>
    internal const string ConnectorContextHeading = "## Connector context (auto-injected by platform)";

    /// <inheritdoc />
    public async Task<string> AssembleAsync(
        PromptAssemblyContext? context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Assembling per-agent system prompt.");

        var builder = new StringBuilder();

        // Layer 1: Platform instructions
        var platform = await platformPromptProvider.GetPlatformPromptAsync(cancellationToken);
        builder.AppendLine("## Platform Instructions");
        builder.AppendLine(platform);
        builder.AppendLine();

        if (context is not null)
        {
            // Layer 1 (continued): connector context. Platform-injected
            // markdown fragments built upstream by IConnectorPromptContextResolver
            // — one per direct + inherited connector binding on the
            // launch subject. Each fragment is expected to start with a
            // `### …` sub-heading naming the binding; the assembler wraps
            // them in a single section header so multiple connectors render
            // cleanly side-by-side. The section is omitted entirely when
            // the resolver returned no fragments.
            if (context.ConnectorPromptFragments is { Count: > 0 } fragments)
            {
                builder.AppendLine(ConnectorContextHeading);
                foreach (var fragment in fragments)
                {
                    if (string.IsNullOrWhiteSpace(fragment))
                    {
                        continue;
                    }

                    builder.AppendLine(fragment.TrimEnd());
                    builder.AppendLine();
                }
            }


            // Layer 2: Unit context — policies, connector-skills,
            // unit-equipped skill bundles.
            var unitContext = unitContextBuilder.Build(
                context.Policies,
                context.Skills,
                context.SkillBundles);

            if (!string.IsNullOrWhiteSpace(unitContext))
            {
                builder.AppendLine("## Unit Context");
                builder.AppendLine(unitContext);
                builder.AppendLine();
            }

            // Layer 4: Agent instructions — user-authored instructions plus
            // any agent-equipped skill bundles. Composed by
            // AgentInstructionsBuilder so the conditional emit of the
            // section header is consistent: when both the user instructions
            // and the bundle list are empty, no "## Agent Instructions"
            // header is rendered.
            var agentInstructions = agentInstructionsBuilder.Build(
                context.AgentInstructions,
                context.AgentSkillBundles);

            if (!string.IsNullOrWhiteSpace(agentInstructions))
            {
                builder.AppendLine("## Agent Instructions");
                builder.AppendLine(agentInstructions);
                builder.AppendLine();
            }
        }

        _logger.LogDebug("Prompt assembly complete.");
        return builder.ToString().TrimEnd();
    }
}
