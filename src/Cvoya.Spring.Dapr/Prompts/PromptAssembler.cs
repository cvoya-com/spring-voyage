// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Prompts;

using System.Text;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;

/// <summary>
/// Assembles the per-agent system prompt by composing three sections:
/// platform instructions, unit context, and role-specific instructions.
/// The platform-instructions section carries — in order — the
/// platform-contract block from <see cref="IPlatformPromptProvider"/>
/// (#2679/#2681), the launch subject's identity section (#2680), the
/// per-runtime container/workspace description (#2682), and the
/// auto-injected connector context. Thread history is delivered by each
/// runtime's session-resume mechanism rather than duplicated in the
/// assembled prompt — see <see cref="PromptAssemblyContext"/> remarks.
/// Stateless and safe to share across concurrent actors — all per-agent
/// state is passed through <see cref="AssembleAsync"/>.
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

    /// <summary>
    /// Heading used for the per-runtime container/workspace section
    /// (#2682). Exposed as a constant so tests and the launcher prose
    /// (which contributes the body, not the heading) can pin against
    /// the same string.
    /// </summary>
    internal const string ContainerAndWorkspaceHeading = "## Container and workspace";

    /// <summary>
    /// Heading used for the role-specific instructions section
    /// (#2684 — renamed from <c>## Agent Instructions</c>). Exposed as
    /// a constant so tests, docs, and downstream consumers can pin
    /// against the same string. Kind-neutral so unit-shaped subjects
    /// see the same header as agent-shaped subjects.
    /// </summary>
    internal const string RoleSpecificInstructionsHeading = "## Role-specific instructions";

    /// <inheritdoc />
    public async Task<string> AssembleAsync(
        PromptAssemblyContext? context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Assembling per-agent system prompt.");

        var builder = new StringBuilder();

        // Platform instructions — platform introduction (#2679) +
        // platform contract (#2681 reads-vs-side-effects + messaging
        // emphasis integrated) + always-available tool catalog (#2670).
        var platform = await platformPromptProvider.GetPlatformPromptAsync(cancellationToken);
        builder.AppendLine("## Platform Instructions");
        builder.AppendLine(platform);
        builder.AppendLine();

        if (context is not null)
        {
            // Identity (#2680). Pre-rendered by
            // IIdentityPromptContextResolver and handed in on the
            // assembly context. The fragment is expected to start with
            // its own `## Who you are` heading; the assembler does NOT
            // wrap it (the resolver owns layout choices). Omitted when
            // the resolver returned null / empty.
            if (!string.IsNullOrWhiteSpace(context.IdentityPromptFragment))
            {
                builder.AppendLine(context.IdentityPromptFragment.TrimEnd());
                builder.AppendLine();
            }

            // Per-runtime container/workspace description (#2682).
            // Contributed by each IAgentRuntimeLauncher via
            // GetWorkspacePromptFragment; the assembler renders it
            // under a fixed heading so launcher prose stays body-only
            // and the heading stays a single pinnable string. Omitted
            // entirely when the launcher contributed nothing (the
            // A2A-native Spring Voyage agent).
            if (!string.IsNullOrWhiteSpace(context.WorkspacePromptFragment))
            {
                builder.AppendLine(ContainerAndWorkspaceHeading);
                builder.AppendLine(context.WorkspacePromptFragment.TrimEnd());
                builder.AppendLine();
            }

            // Connector context. Platform-injected markdown fragments
            // built upstream by IConnectorPromptContextResolver — one
            // per direct + inherited connector binding on the launch
            // subject. Each fragment is expected to start with a
            // `### …` sub-heading naming the binding; the assembler
            // wraps them in a single section header so multiple
            // connectors render cleanly side-by-side. The section is
            // omitted entirely when the resolver returned no fragments.
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


            // Unit context — policies and unit-equipped skill bundles.
            // The per-registry skill listing was removed in #2670: the
            // always-available platform-tool catalog lives in the
            // platform-instructions section above, and connector
            // context rides the connector-context subsection just
            // above this one.
            var unitContext = unitContextBuilder.Build(
                context.Policies,
                context.SkillBundles);

            if (!string.IsNullOrWhiteSpace(unitContext))
            {
                builder.AppendLine("## Unit Context");
                builder.AppendLine(unitContext);
                builder.AppendLine();
            }

            // Role-specific instructions — user-authored instructions
            // plus any agent-equipped skill bundles. The heading is
            // kind-neutral (#2684) so unit-shaped subjects render the
            // same way as agent-shaped subjects, and the section name
            // describes what the content *is* rather than who authored
            // it. Composed by AgentInstructionsBuilder so the
            // conditional emit of the section header is consistent:
            // when both the user instructions and the bundle list are
            // empty, no heading is rendered.
            var agentInstructions = agentInstructionsBuilder.Build(
                context.AgentInstructions,
                context.AgentSkillBundles);

            if (!string.IsNullOrWhiteSpace(agentInstructions))
            {
                builder.AppendLine(RoleSpecificInstructionsHeading);
                builder.AppendLine(agentInstructions);
                builder.AppendLine();
            }
        }

        _logger.LogDebug("Prompt assembly complete.");
        return builder.ToString().TrimEnd();
    }
}
