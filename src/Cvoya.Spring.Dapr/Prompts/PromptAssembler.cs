// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Prompts;

using System.Text;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// Assembles prompts by composing four layers: platform instructions, unit context,
/// thread context, and agent instructions. The output is the system-prompt text
/// handed to the external agent runtime by <see cref="IExecutionDispatcher"/>.
/// Stateless and safe to share across concurrent actors — all per-invocation state is
/// passed through <see cref="AssembleAsync"/>.
/// </summary>
public class PromptAssembler(
    IPlatformPromptProvider platformPromptProvider,
    UnitContextBuilder unitContextBuilder,
    ThreadContextBuilder threadContextBuilder,
    AgentInstructionsBuilder agentInstructionsBuilder,
    ILoggerFactory loggerFactory) : IPromptAssembler
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<PromptAssembler>();

    /// <summary>
    /// Heading used for the platform-injected connector-context
    /// subsection (#2442). Exposed as a constant so tests and downstream
    /// docs can pin the exact rendered text.
    /// </summary>
    internal const string ConnectorContextHeading = "## Connector context (auto-injected by platform)";

    /// <inheritdoc />
    public async Task<string> AssembleAsync(
        Message message,
        PromptAssemblyContext? context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Assembling prompt for message {MessageId}.", message.Id);

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
            // (#2442) — one per direct + inherited connector binding on the
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

            // Layer 3: Thread context
            // Pass the pre-resolved sender display-name map (#2129) so the
            // builder can fold raw scheme:<guid> sender prefixes down to
            // human-readable names. The actor builds the map upstream where
            // it has scoped access to IParticipantDisplayNameResolver; the
            // assembler stays singleton-safe.
            var threadContext = threadContextBuilder.Build(
                context.PriorMessages,
                context.LastCheckpoint,
                context.PriorMessageSenderDisplayNames);

            if (!string.IsNullOrWhiteSpace(threadContext))
            {
                builder.AppendLine("## Thread Context");
                builder.AppendLine(threadContext);
                builder.AppendLine();
            }

            // Layer 4: Agent instructions — user-authored instructions plus
            // any agent-equipped skill bundles (#2360). Composed by
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

        _logger.LogDebug("Prompt assembly complete for message {MessageId}.", message.Id);
        return builder.ToString().TrimEnd();
    }
}
