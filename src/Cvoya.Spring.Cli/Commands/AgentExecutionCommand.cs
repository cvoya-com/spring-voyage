// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring agent execution get|set|clear</c> verb subtree
/// (#601 / #603 / #409 B-wide). Symmetric with <see cref="UnitExecutionCommand"/>
/// — same five fields plus the agent-exclusive <c>--hosting</c> flag
/// (ephemeral / persistent). Operates on the agent's own on-disk block;
/// inherited unit defaults are merged in at dispatch time by the
/// <c>IAgentDefinitionProvider</c>.
/// </summary>
public static class AgentExecutionCommand
{
    internal static readonly string[] HostingKeys = { "ephemeral", "persistent" };

    /// <summary>Entry point. Returns the <c>execution</c> subcommand tree.</summary>
    public static Command Create(Option<string> outputOption)
    {
        var command = new Command(
            "execution",
            "Read / write the agent's on-disk execution block. Fields: image, " +
            "runtime, tool, provider, model, hosting. Missing fields fall back to the parent " +
            "unit's execution defaults at dispatch time.");

        command.Subcommands.Add(CreateGetCommand(outputOption));
        command.Subcommands.Add(CreateSetCommand(outputOption));
        command.Subcommands.Add(CreateClearCommand(outputOption));
        return command;
    }

    private static Command CreateGetCommand(Option<string> outputOption)
    {
        var agentArg = new Argument<string>("agent") { Description = "The agent identifier" };
        var command = new Command(
            "get",
            "Print the agent's own declared execution block. Does NOT show inherited unit " +
            "defaults — null fields indicate either an unset agent-level slot or a slot that " +
            "will resolve from the parent unit at dispatch.");
        command.Arguments.Add(agentArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var agentId = parseResult.GetValue(agentArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var shape = await client.GetAgentExecutionAsync(agentId, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    agent = agentId,
                    image = shape.Image,
                    runtime = shape.Runtime,
                    agent_runtime = shape.Agent,
                    tool_kind = shape.ToolKind,
                    provider = shape.Provider,
                    model = shape.Model,
                    hosting = shape.Hosting,
                }));
                return;
            }

            Console.WriteLine($"Agent:    {agentId}");
            Console.WriteLine($"  image:         {shape.Image ?? "(inherited / unset)"}");
            Console.WriteLine($"  runtime:       {shape.Runtime ?? "(inherited / unset)"}");
            Console.WriteLine($"  agent_runtime: {shape.Agent ?? "(inherited / unset)"}");
            // #1732: tool_kind is read-only — derived from agent_runtime via
            // the runtime registry.
            Console.WriteLine($"  tool_kind:     {shape.ToolKind ?? "(derived from agent_runtime)"}");
            Console.WriteLine($"  provider:      {shape.Provider ?? "(inherited / unset)"}");
            Console.WriteLine($"  model:         {shape.Model ?? "(inherited / unset)"}");
            Console.WriteLine($"  hosting:       {shape.Hosting ?? "(default: ephemeral)"}");
        });

        return command;
    }

    private static Command CreateSetCommand(Option<string> outputOption)
    {
        var agentArg = new Argument<string>("agent") { Description = "The agent identifier" };
        var imageOption = new Option<string?>("--image")
        {
            Description = "Container image reference.",
        };
        var runtimeOption = new Option<string?>("--runtime")
        {
            Description = "Container runtime. Allowed values: " + string.Join(", ", UnitExecutionCommand.RuntimeKeys) + ".",
        };
        runtimeOption.AcceptOnlyFromAmong(UnitExecutionCommand.RuntimeKeys);

        var agentRuntimeOption = new Option<string?>("--agent")
        {
            Description = "Agent runtime registry id (e.g. claude, openai, google, ollama). " +
                "Drives launcher selection at dispatch via IAgentRuntime.ToolKind.",
        };

        var providerOption = new Option<string?>("--provider")
        {
            Description = "LLM provider (Spring Voyage Agent–specific).",
        };
        var modelOption = new Option<string?>("--model")
        {
            Description = "Model identifier (Spring Voyage Agent–specific).",
        };
        var hostingOption = new Option<string?>("--hosting")
        {
            Description = "Hosting mode. Allowed values: " + string.Join(", ", HostingKeys) + ". Agent-exclusive (never inherits).",
        };
        hostingOption.AcceptOnlyFromAmong(HostingKeys);

        var command = new Command(
            "set",
            "Upsert one or more fields on the agent's execution block. Partial update — " +
            "pass only the flags you want to change.");
        command.Arguments.Add(agentArg);
        command.Options.Add(imageOption);
        command.Options.Add(runtimeOption);
        command.Options.Add(agentRuntimeOption);
        command.Options.Add(providerOption);
        command.Options.Add(modelOption);
        command.Options.Add(hostingOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var agentId = parseResult.GetValue(agentArg)!;
            var image = parseResult.GetValue(imageOption);
            var runtime = parseResult.GetValue(runtimeOption);
            var agentRuntime = parseResult.GetValue(agentRuntimeOption);
            var provider = parseResult.GetValue(providerOption);
            var model = parseResult.GetValue(modelOption);
            var hosting = parseResult.GetValue(hostingOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (string.IsNullOrWhiteSpace(image) && string.IsNullOrWhiteSpace(runtime)
                && string.IsNullOrWhiteSpace(agentRuntime) && string.IsNullOrWhiteSpace(provider)
                && string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(hosting))
            {
                await Console.Error.WriteLineAsync(
                    "Nothing to set. Pass at least one of --image, --runtime, --agent, --provider, --model, --hosting.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();

            var stored = await client.SetAgentExecutionAsync(agentId, new AgentExecutionResponse
            {
                Image = image,
                Runtime = runtime,
                Agent = agentRuntime,
                Provider = provider,
                Model = model,
                Hosting = hosting,
            }, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    agent = agentId,
                    image = stored.Image,
                    runtime = stored.Runtime,
                    agent_runtime = stored.Agent,
                    tool_kind = stored.ToolKind,
                    provider = stored.Provider,
                    model = stored.Model,
                    hosting = stored.Hosting,
                }));
            }
            else
            {
                Console.WriteLine($"Agent '{agentId}' execution updated.");
                Console.WriteLine($"  image:         {stored.Image ?? "(inherited / unset)"}");
                Console.WriteLine($"  runtime:       {stored.Runtime ?? "(inherited / unset)"}");
                Console.WriteLine($"  agent_runtime: {stored.Agent ?? "(inherited / unset)"}");
                Console.WriteLine($"  tool_kind:     {stored.ToolKind ?? "(derived from agent_runtime)"}");
                Console.WriteLine($"  provider:      {stored.Provider ?? "(inherited / unset)"}");
                Console.WriteLine($"  model:         {stored.Model ?? "(inherited / unset)"}");
                Console.WriteLine($"  hosting:       {stored.Hosting ?? "(default: ephemeral)"}");
            }
        });

        return command;
    }

    private static Command CreateClearCommand(Option<string> outputOption)
    {
        var agentArg = new Argument<string>("agent") { Description = "The agent identifier" };
        // #1732: tool was dropped — agent (the runtime registry id) is the
        // input; tool_kind is derived from it server-side.
        var fieldKeys = new[] { "image", "runtime", "agent", "provider", "model", "hosting" };
        var fieldOption = new Option<string?>("--field")
        {
            Description = "Clear one field only. Allowed: " + string.Join(", ", fieldKeys) + ". " +
                "When omitted the entire block is stripped.",
        };
        fieldOption.AcceptOnlyFromAmong(fieldKeys);

        var command = new Command(
            "clear",
            "Remove the agent's execution block (or a single field when --field is set).");
        command.Arguments.Add(agentArg);
        command.Options.Add(fieldOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var agentId = parseResult.GetValue(agentArg)!;
            var field = parseResult.GetValue(fieldOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            if (string.IsNullOrWhiteSpace(field))
            {
                await client.ClearAgentExecutionAsync(agentId, ct);
                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJsonPlain(new { agent = agentId, image = (string?)null }));
                }
                else
                {
                    Console.WriteLine($"Agent '{agentId}' execution block cleared.");
                }
                return;
            }

            // Per-field clear: re-PUT every other field (same pattern as
            // UnitExecutionCommand). Falls through to block-clear when
            // the remaining state is empty.
            var current = await client.GetAgentExecutionAsync(agentId, ct);
            var updated = new AgentExecutionResponse
            {
                Image = string.Equals(field, "image", StringComparison.OrdinalIgnoreCase) ? null : current.Image,
                Runtime = string.Equals(field, "runtime", StringComparison.OrdinalIgnoreCase) ? null : current.Runtime,
                Agent = string.Equals(field, "agent", StringComparison.OrdinalIgnoreCase) ? null : current.Agent,
                Provider = string.Equals(field, "provider", StringComparison.OrdinalIgnoreCase) ? null : current.Provider,
                Model = string.Equals(field, "model", StringComparison.OrdinalIgnoreCase) ? null : current.Model,
                Hosting = string.Equals(field, "hosting", StringComparison.OrdinalIgnoreCase) ? null : current.Hosting,
            };

            if (string.IsNullOrWhiteSpace(updated.Image) && string.IsNullOrWhiteSpace(updated.Runtime)
                && string.IsNullOrWhiteSpace(updated.Agent) && string.IsNullOrWhiteSpace(updated.Provider)
                && string.IsNullOrWhiteSpace(updated.Model) && string.IsNullOrWhiteSpace(updated.Hosting))
            {
                await client.ClearAgentExecutionAsync(agentId, ct);
            }
            else
            {
                await client.ClearAgentExecutionAsync(agentId, ct);
                await client.SetAgentExecutionAsync(agentId, updated, ct);
            }

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new { agent = agentId, cleared = field }));
            }
            else
            {
                Console.WriteLine($"Agent '{agentId}' execution.{field} cleared.");
            }
        });

        return command;
    }
}