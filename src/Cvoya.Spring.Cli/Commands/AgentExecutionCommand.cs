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
/// agent-exclusive <c>--hosting</c> flag (ephemeral / persistent) and
/// (since #2693) the prompt-shaping <c>--system-prompt-mode</c> flag.
/// Operates on the agent's own on-disk block; inherited unit defaults
/// are merged in at dispatch time by the
/// <c>IAgentDefinitionProvider</c>.
/// </summary>
public static class AgentExecutionCommand
{
    internal static readonly string[] HostingKeys = { "ephemeral", "persistent" };

    /// <summary>
    /// Allowed values for <c>--system-prompt-mode</c> (#2693 / #2692).
    /// Wire form is the lower-case enum literal; the server validates the
    /// same set on the PATCH boundary so we mirror it here to fail fast
    /// before the round-trip.
    /// </summary>
    internal static readonly string[] SystemPromptModeKeys = { "append", "replace" };

    /// <summary>Entry point. Returns the <c>execution</c> subcommand tree.</summary>
    public static Command Create(Option<string> outputOption)
    {
        var command = new Command(
            "execution",
            "Read / write the agent's on-disk execution block. Fields: image, runtime, " +
            "model-provider, model, hosting, system-prompt-mode. Missing fields fall back to the " +
            "parent unit's execution defaults at dispatch time (system-prompt-mode further falls " +
            "back to the platform default 'append' when no unit value is declared).");

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
            "defaults for image / runtime / model / hosting — null fields indicate either an " +
            "unset agent-level slot or a slot that will resolve from the parent unit at dispatch. " +
            "For system-prompt-mode both the declared (agent-own) and effective (post-cascade) " +
            "values are printed so operators can see what the dispatcher will actually use.");
        command.Arguments.Add(agentArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(agentArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);
            string agentId;
            try
            {
                agentId = await resolver.ResolveAgentIdAsync(idOrName, unitContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            var shape = await client.GetAgentExecutionAsync(agentId, ct);
            // #2693: fetch the agent detail to read the (resolved, declared)
            // systemPromptMode pair. PR E surfaces these on AgentResponse:
            // `systemPromptMode` carries the post-cascade effective value
            // (agent → unit → platform default 'append'); `declaredSystemPromptMode`
            // carries the agent's own on-disk literal (null when the slot
            // is unset and the cascade is in play).
            var detail = await client.GetAgentStatusAsync(agentId, ct);
            var declaredSpm = detail.Agent?.DeclaredSystemPromptMode;
            var effectiveSpm = detail.Agent?.SystemPromptMode;

            // ADR-0038: the wire shape is { image, runtime, model: {provider, id}, hosting }.
            // ADR-0039 §7: containerRuntime slot removed.
            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    agent = agentId,
                    image = shape.Image,
                    runtime = shape.Runtime,
                    model_provider = shape.Model?.AiModelDto?.Provider,
                    model = shape.Model?.AiModelDto?.Id,
                    hosting = shape.Hosting,
                    declared_system_prompt_mode = declaredSpm,
                    system_prompt_mode = effectiveSpm,
                }));
                return;
            }

            Console.WriteLine($"Agent:    {idOrName}");
            Console.WriteLine($"  image:                          {shape.Image ?? "(inherited / unset)"}");
            Console.WriteLine($"  runtime:                        {shape.Runtime ?? "(inherited / unset)"}");
            Console.WriteLine($"  model_provider:                 {shape.Model?.AiModelDto?.Provider ?? "(inherited / unset)"}");
            Console.WriteLine($"  model:                          {shape.Model?.AiModelDto?.Id ?? "(inherited / unset)"}");
            Console.WriteLine($"  hosting:                        {shape.Hosting ?? "(default: ephemeral)"}");
            Console.WriteLine($"  system_prompt_mode (declared):  {declaredSpm ?? "(inherited / unset)"}");
            Console.WriteLine($"  system_prompt_mode (effective): {effectiveSpm ?? "(default: append)"}");
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

        // #2693 / #2692 / #2691: prompt-shaping flag. Replaces ⇒ the
        // platform-assembled system prompt becomes the entire system
        // prompt sent to the model; appends ⇒ it is concatenated after
        // the runtime's own default system prompt. Cascade: agent →
        // parent unit → platform default 'append'.
        var systemPromptModeOption = new Option<string?>("--system-prompt-mode")
        {
            Description = "How the platform-assembled system prompt combines with the runtime's " +
                "own default. Allowed values: " + string.Join(", ", SystemPromptModeKeys) + ". " +
                "Cascade when null: parent-unit value, else platform default 'append'.",
        };
        systemPromptModeOption.AcceptOnlyFromAmong(SystemPromptModeKeys);

        var command = new Command(
            "set",
            "Upsert one or more fields on the agent's execution block. Partial update — " +
            "pass only the flags you want to change. The system-prompt-mode slot rides a " +
            "separate PATCH (it lives on the agent's persisted definition rather than the " +
            "execution shape) but the CLI exposes it here for symmetry with the other " +
            "prompt-pipeline knobs.");
        command.Arguments.Add(agentArg);
        command.Options.Add(imageOption);
        command.Options.Add(runtimeOption);
        command.Options.Add(modelProviderOption);
        command.Options.Add(modelOption);
        command.Options.Add(hostingOption);
        command.Options.Add(systemPromptModeOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(agentArg)!;
            var image = parseResult.GetValue(imageOption);
            var runtime = parseResult.GetValue(runtimeOption);
            var modelProvider = parseResult.GetValue(modelProviderOption);
            var model = parseResult.GetValue(modelOption);
            var hosting = parseResult.GetValue(hostingOption);
            var systemPromptMode = parseResult.GetValue(systemPromptModeOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (string.IsNullOrWhiteSpace(image)
                && string.IsNullOrWhiteSpace(runtime) && string.IsNullOrWhiteSpace(modelProvider)
                && string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(hosting)
                && string.IsNullOrWhiteSpace(systemPromptMode))
            {
                await Console.Error.WriteLineAsync(
                    "Nothing to set. Pass at least one of --image, --runtime, --model-provider, " +
                    "--model, --hosting, --system-prompt-mode.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);
            string agentId;
            try
            {
                agentId = await resolver.ResolveAgentIdAsync(idOrName, unitContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            // ADR-0038: structured execution.model = {provider, id} on the wire.
            var modelDto = (!string.IsNullOrWhiteSpace(modelProvider) && !string.IsNullOrWhiteSpace(model))
                ? new AgentExecutionResponse.AgentExecutionResponse_model
                {
                    AiModelDto = new AiModelDto { Provider = modelProvider, Id = model },
                }
                : null;

            // #2693: send the execution-block fields through PUT only when
            // at least one was supplied. An all-null PUT body is rejected
            // server-side, and the systemPromptMode path lives on a
            // separate PATCH endpoint (UpdateAgentMetadataRequest, PR E /
            // #2714) so we never want a no-op PUT when the operator only
            // touched --system-prompt-mode.
            AgentExecutionResponse? stored = null;
            var executionBlockTouched = !string.IsNullOrWhiteSpace(image)
                || !string.IsNullOrWhiteSpace(runtime)
                || modelDto is not null
                || !string.IsNullOrWhiteSpace(hosting);

            if (executionBlockTouched)
            {
                stored = await client.SetAgentExecutionAsync(agentId, new AgentExecutionResponse
                {
                    Image = image,
                    Runtime = runtime,
                    Model = modelDto,
                    Hosting = hosting,
                }, ct);
            }

            if (!string.IsNullOrWhiteSpace(systemPromptMode))
            {
                await client.SetAgentSystemPromptModeAsync(agentId, systemPromptMode, ct);
            }

            // Re-read so the rendered output reflects post-PATCH state for
            // every slot, including systemPromptMode (which would otherwise
            // be invisible on the execution-only GET response).
            stored ??= await client.GetAgentExecutionAsync(agentId, ct);
            var detail = await client.GetAgentStatusAsync(agentId, ct);
            var declaredSpm = detail.Agent?.DeclaredSystemPromptMode;
            var effectiveSpm = detail.Agent?.SystemPromptMode;

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    agent = agentId,
                    image = stored.Image,
                    runtime = stored.Runtime,
                    model_provider = stored.Model?.AiModelDto?.Provider,
                    model = stored.Model?.AiModelDto?.Id,
                    hosting = stored.Hosting,
                    declared_system_prompt_mode = declaredSpm,
                    system_prompt_mode = effectiveSpm,
                }));
            }
            else
            {
                Console.WriteLine($"Agent '{idOrName}' execution updated.");
                Console.WriteLine($"  image:                          {stored.Image ?? "(inherited / unset)"}");
                Console.WriteLine($"  runtime:                        {stored.Runtime ?? "(inherited / unset)"}");
                Console.WriteLine($"  model_provider:                 {stored.Model?.AiModelDto?.Provider ?? "(inherited / unset)"}");
                Console.WriteLine($"  model:                          {stored.Model?.AiModelDto?.Id ?? "(inherited / unset)"}");
                Console.WriteLine($"  hosting:                        {stored.Hosting ?? "(default: ephemeral)"}");
                Console.WriteLine($"  system_prompt_mode (declared):  {declaredSpm ?? "(inherited / unset)"}");
                Console.WriteLine($"  system_prompt_mode (effective): {effectiveSpm ?? "(default: append)"}");
            }
        });

        return command;
    }

    private static Command CreateClearCommand(Option<string> outputOption)
    {
        var agentArg = new Argument<string>("agent") { Description = "The agent identifier" };
        // ADR-0038: the field-key surface is
        // (image, runtime, model-provider, model, hosting).
        // ADR-0039 §7: container-runtime removed from the surface.
        var fieldKeys = new[]
        {
            "image", "runtime", "model-provider", "model", "hosting",
        };
        var fieldOption = new Option<string?>("--field")
        {
            Description = "Clear one execution-block field only. Allowed: " + string.Join(", ", fieldKeys) + ". " +
                "When omitted (and --system-prompt-mode is not set) the entire block is stripped.",
        };
        fieldOption.AcceptOnlyFromAmong(fieldKeys);

        // #2693: dedicated boolean flag to clear the prompt-shaping slot.
        // Lives on a separate PATCH endpoint
        // (UpdateAgentMetadataRequest.SystemPromptMode, PR E / #2714)
        // rather than the execution-block PUT, so it is its own flag
        // rather than another --field value.
        var systemPromptModeOption = new Option<bool>("--system-prompt-mode")
        {
            Description = "Clear the agent's declared system-prompt-mode (PATCH with explicit JSON null). " +
                "The dispatcher then resolves the slot through the cascade: parent-unit value, " +
                "else platform default 'append'.",
        };

        var command = new Command(
            "clear",
            "Remove the agent's execution block (or a single field when --field is set). " +
            "Pass --system-prompt-mode to clear just the prompt-shaping slot via PATCH; that " +
            "flag is independent of --field and does not touch the execution block.");
        command.Arguments.Add(agentArg);
        command.Options.Add(fieldOption);
        command.Options.Add(systemPromptModeOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(agentArg)!;
            var field = parseResult.GetValue(fieldOption);
            var clearSystemPromptMode = parseResult.GetValue(systemPromptModeOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            // The two clearable surfaces (execution block vs. systemPromptMode)
            // live on different endpoints. Combining --field with
            // --system-prompt-mode would mean "clear one execution slot AND
            // PATCH-clear systemPromptMode in the same invocation", which is
            // surprising and undocumented. Reject the combination with an
            // exit-1 user error and let the operator issue two clear
            // commands explicitly.
            if (clearSystemPromptMode && !string.IsNullOrWhiteSpace(field))
            {
                await Console.Error.WriteLineAsync(
                    "--system-prompt-mode and --field cannot be combined. Issue two `clear` " +
                    "commands (one per slot) instead.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);
            string agentId;
            try
            {
                agentId = await resolver.ResolveAgentIdAsync(idOrName, unitContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            if (clearSystemPromptMode)
            {
                // PATCH with explicit JSON null per PR E's tri-state on
                // UpdateAgentMetadataRequest.SystemPromptMode (absent /
                // explicit-null / value). Explicit-null clears the slot
                // and re-engages the cascade.
                await client.SetAgentSystemPromptModeAsync(agentId, systemPromptMode: null, ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                    {
                        agent = agentId,
                        cleared = "system-prompt-mode",
                    }));
                }
                else
                {
                    Console.WriteLine($"Agent '{idOrName}' execution.system_prompt_mode cleared.");
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(field))
            {
                await client.ClearAgentExecutionAsync(agentId, ct);
                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJsonPlain(new { agent = agentId, image = (string?)null }));
                }
                else
                {
                    Console.WriteLine($"Agent '{idOrName}' execution block cleared.");
                }
                return;
            }

            // Per-field clear: re-PUT every other field (same pattern as
            // UnitExecutionCommand). Falls through to block-clear when
            // the remaining state is empty.
            var current = await client.GetAgentExecutionAsync(agentId, ct);
            // ADR-0038: per-field clear targets one of
            // { image | runtime | model-provider | model | hosting }.
            // ADR-0039 §7: container-runtime removed.
            // model-provider clears just the provider half of the structured
            // execution.model; clearing model wipes the whole {provider, id} pair.
            var keepImage = !string.Equals(field, "image", StringComparison.OrdinalIgnoreCase);
            var keepRuntime = !string.Equals(field, "runtime", StringComparison.OrdinalIgnoreCase);
            var keepModel = !string.Equals(field, "model", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(field, "model-provider", StringComparison.OrdinalIgnoreCase);
            var keepHosting = !string.Equals(field, "hosting", StringComparison.OrdinalIgnoreCase);

            var updated = new AgentExecutionResponse
            {
                Image = keepImage ? current.Image : null,
                Runtime = keepRuntime ? current.Runtime : null,
                Model = keepModel ? current.Model : null,
                Hosting = keepHosting ? current.Hosting : null,
            };

            if (string.IsNullOrWhiteSpace(updated.Image)
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
                Console.WriteLine($"Agent '{idOrName}' execution.{field} cleared.");
            }
        });

        return command;
    }
}
