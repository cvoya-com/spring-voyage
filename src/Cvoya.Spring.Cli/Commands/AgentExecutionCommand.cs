// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring agent execution get|set|clear</c> verb subtree
/// (#601 / #603 / #409 B-wide; ADR-0038). Symmetric with
/// <see cref="UnitExecutionCommand"/> — same set of fields plus the
/// agent-exclusive <c>--hosting</c> flag (ephemeral / persistent).
/// Operates on the agent's own on-disk block; inherited unit defaults
/// are merged in at dispatch time by the
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
            "container-runtime, runtime, model-provider, model, hosting. Missing fields fall back to " +
            "the parent unit's execution defaults at dispatch time.");

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

            // ADR-0038: the wire shape is { image, containerRuntime,
            // runtime, model: {provider, id}, hosting }.
            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    agent = agentId,
                    image = shape.Image,
                    container_runtime = shape.ContainerRuntime,
                    runtime = shape.Runtime,
                    model_provider = shape.Model?.AiModelDto?.Provider,
                    model = shape.Model?.AiModelDto?.Id,
                    hosting = shape.Hosting,
                }));
                return;
            }

            Console.WriteLine($"Agent:    {agentId}");
            Console.WriteLine($"  image:             {shape.Image ?? "(inherited / unset)"}");
            Console.WriteLine($"  container_runtime: {shape.ContainerRuntime ?? "(inherited / unset)"}");
            Console.WriteLine($"  runtime:           {shape.Runtime ?? "(inherited / unset)"}");
            Console.WriteLine($"  model_provider:    {shape.Model?.AiModelDto?.Provider ?? "(inherited / unset)"}");
            Console.WriteLine($"  model:             {shape.Model?.AiModelDto?.Id ?? "(inherited / unset)"}");
            Console.WriteLine($"  hosting:           {shape.Hosting ?? "(default: ephemeral)"}");
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
        var containerRuntimeOption = new Option<string?>("--container-runtime")
        {
            Description = "Container runtime. Allowed values: " + string.Join(", ", UnitExecutionCommand.ContainerRuntimeKeys) + ".",
        };
        containerRuntimeOption.AcceptOnlyFromAmong(UnitExecutionCommand.ContainerRuntimeKeys);

        var runtimeOption = new Option<string?>("--runtime")
        {
            Description = "Agent runtime id (e.g. claude-code, codex, gemini, spring-voyage). " +
                "Drives launcher selection at dispatch.",
        };

        var modelProviderOption = new Option<string?>("--model-provider")
        {
            Description = "Model-provider id (e.g. anthropic, openai, google, ollama). " +
                "Required for multi-provider runtimes (spring-voyage); optional otherwise.",
        };
        var modelOption = new Option<string?>("--model")
        {
            Description = "Model id (the structured execution.model.id).",
        };
        var hostingOption = new Option<string?>("--hosting")
        {
            Description = "Hosting mode. Allowed values: " + string.Join(", ", HostingKeys) + ". Agent-exclusive (never inherits).",
        };
        hostingOption.AcceptOnlyFromAmong(HostingKeys);

        // ADR-0038 §7: legacy `--agent` rejected at parse time.
        var legacyAgentOption = new Option<string?>("--agent")
        {
            Description = "REJECTED — use --runtime instead (ADR-0038).",
            Hidden = true,
        };
        legacyAgentOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0)
            {
                result.AddError(AgentCommand.LegacyAgentFlagRejectionMessage);
            }
        });
        // ADR-0038: flat `--provider` is gone; use --model-provider.
        var legacyProviderOption = new Option<string?>("--provider")
        {
            Description = "REJECTED — use --model-provider instead (ADR-0038).",
            Hidden = true,
        };
        legacyProviderOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0)
            {
                result.AddError(UnitExecutionCommand.LegacyProviderFlagRejectionMessage);
            }
        });

        var command = new Command(
            "set",
            "Upsert one or more fields on the agent's execution block. Partial update — " +
            "pass only the flags you want to change.");
        command.Arguments.Add(agentArg);
        command.Options.Add(imageOption);
        command.Options.Add(containerRuntimeOption);
        command.Options.Add(runtimeOption);
        command.Options.Add(modelProviderOption);
        command.Options.Add(modelOption);
        command.Options.Add(hostingOption);
        command.Options.Add(legacyAgentOption);
        command.Options.Add(legacyProviderOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var agentId = parseResult.GetValue(agentArg)!;
            var image = parseResult.GetValue(imageOption);
            var containerRuntime = parseResult.GetValue(containerRuntimeOption);
            var runtime = parseResult.GetValue(runtimeOption);
            var modelProvider = parseResult.GetValue(modelProviderOption);
            var model = parseResult.GetValue(modelOption);
            var hosting = parseResult.GetValue(hostingOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (string.IsNullOrWhiteSpace(image) && string.IsNullOrWhiteSpace(containerRuntime)
                && string.IsNullOrWhiteSpace(runtime) && string.IsNullOrWhiteSpace(modelProvider)
                && string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(hosting))
            {
                await Console.Error.WriteLineAsync(
                    "Nothing to set. Pass at least one of --image, --container-runtime, --runtime, --model-provider, --model, --hosting.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();

            // ADR-0038: structured execution.model = {provider, id} on the wire.
            var modelDto = (!string.IsNullOrWhiteSpace(modelProvider) && !string.IsNullOrWhiteSpace(model))
                ? new AgentExecutionResponse.AgentExecutionResponse_model
                {
                    AiModelDto = new AiModelDto { Provider = modelProvider, Id = model },
                }
                : null;

            var stored = await client.SetAgentExecutionAsync(agentId, new AgentExecutionResponse
            {
                Image = image,
                ContainerRuntime = containerRuntime,
                Runtime = runtime,
                Model = modelDto,
                Hosting = hosting,
            }, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    agent = agentId,
                    image = stored.Image,
                    container_runtime = stored.ContainerRuntime,
                    runtime = stored.Runtime,
                    model_provider = stored.Model?.AiModelDto?.Provider,
                    model = stored.Model?.AiModelDto?.Id,
                    hosting = stored.Hosting,
                }));
            }
            else
            {
                Console.WriteLine($"Agent '{agentId}' execution updated.");
                Console.WriteLine($"  image:             {stored.Image ?? "(inherited / unset)"}");
                Console.WriteLine($"  container_runtime: {stored.ContainerRuntime ?? "(inherited / unset)"}");
                Console.WriteLine($"  runtime:           {stored.Runtime ?? "(inherited / unset)"}");
                Console.WriteLine($"  model_provider:    {stored.Model?.AiModelDto?.Provider ?? "(inherited / unset)"}");
                Console.WriteLine($"  model:             {stored.Model?.AiModelDto?.Id ?? "(inherited / unset)"}");
                Console.WriteLine($"  hosting:           {stored.Hosting ?? "(default: ephemeral)"}");
            }
        });

        return command;
    }

    private static Command CreateClearCommand(Option<string> outputOption)
    {
        var agentArg = new Argument<string>("agent") { Description = "The agent identifier" };
        // ADR-0038: the field-key surface is
        // (image, container-runtime, runtime, model-provider, model, hosting).
        var fieldKeys = new[]
        {
            "image", "container-runtime", "runtime", "model-provider", "model", "hosting",
        };
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
            // ADR-0038: per-field clear targets one of
            // { image | container-runtime | runtime | model-provider | model | hosting }.
            // model-provider clears just the provider half of the structured
            // execution.model; clearing model wipes the whole {provider, id} pair.
            var keepImage = !string.Equals(field, "image", StringComparison.OrdinalIgnoreCase);
            var keepContainerRuntime = !string.Equals(field, "container-runtime", StringComparison.OrdinalIgnoreCase);
            var keepRuntime = !string.Equals(field, "runtime", StringComparison.OrdinalIgnoreCase);
            var keepModel = !string.Equals(field, "model", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(field, "model-provider", StringComparison.OrdinalIgnoreCase);
            var keepHosting = !string.Equals(field, "hosting", StringComparison.OrdinalIgnoreCase);

            var updated = new AgentExecutionResponse
            {
                Image = keepImage ? current.Image : null,
                ContainerRuntime = keepContainerRuntime ? current.ContainerRuntime : null,
                Runtime = keepRuntime ? current.Runtime : null,
                Model = keepModel ? current.Model : null,
                Hosting = keepHosting ? current.Hosting : null,
            };

            if (string.IsNullOrWhiteSpace(updated.Image) && string.IsNullOrWhiteSpace(updated.ContainerRuntime)
                && string.IsNullOrWhiteSpace(updated.Runtime) && updated.Model is null
                && string.IsNullOrWhiteSpace(updated.Hosting))
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