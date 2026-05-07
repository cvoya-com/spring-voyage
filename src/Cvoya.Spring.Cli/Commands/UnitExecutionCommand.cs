// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring unit execution get|set|clear</c> verb subtree
/// (#601 / #603 / #409 B-wide). Direct read/write access to the
/// manifest-persisted unit <c>execution:</c> block (image / runtime /
/// tool / provider / model) without needing a full <c>spring apply -f
/// unit.yaml</c> re-apply. Wraps
/// <see cref="SpringApiClient.GetUnitExecutionAsync(string, System.Threading.CancellationToken)"/>
/// et al so UI / CLI parity is identical to the Execution tab delivered
/// in the follow-up portal PR.
/// </summary>
/// <remarks>
/// <para>
/// Each field is independently settable and independently clearable.
/// <c>set</c> performs a partial update — pass only the flags you want
/// to change. <c>clear</c> strips the whole block; <c>clear --field X</c>
/// clears one field only.
/// </para>
/// <para>
/// <c>--provider</c> is meaningful only when <c>--tool spring-voyage</c>
/// is set on the unit (or the agent inheriting from it); the other
/// tools bake their provider in. <c>--model</c> is meaningful for every
/// tool that carries a known provider family — <c>claude-code</c>
/// (Anthropic), <c>codex</c> (OpenAI), <c>gemini</c> (Google), and
/// <c>spring-voyage</c> — and the CLI treats the value as opaque per the
/// #644 parity fix. The <c>set</c> verb does not enforce either rule
/// today (no whitelist on the server either) so the gating behaviour
/// lives in one place (<c>UnitCommand.ValidateProviderModelAgainstTool</c>);
/// see <c>docs/architecture/cli-and-web.md § Provider + Model flag
/// validation</c>.
/// </para>
/// </remarks>
public static class UnitExecutionCommand
{
    /// <summary>Container runtime keys offered on <c>--runtime</c>.</summary>
    internal static readonly string[] RuntimeKeys = { "docker", "podman" };

    /// <summary>Field keys accepted on <c>clear --field</c>.</summary>
    /// <remarks>
    /// #1732: <c>tool</c> was dropped — the execution tool is derived
    /// 1:1 from <c>agent</c> via the runtime registry's
    /// <c>IAgentRuntime.Kind</c>.
    /// </remarks>
    internal static readonly string[] FieldKeys =
    {
        "image", "runtime", "agent", "provider", "model",
    };

    /// <summary>
    /// Entry point. Returns the <c>execution</c> subcommand tree for
    /// attachment under <c>unit</c>.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var command = new Command(
            "execution",
            "Read / write the unit's manifest-persisted execution defaults. " +
            "Fields: image, runtime, tool, provider, model. Inherited by member agents that " +
            "don't declare their own value per the agent → unit → fail resolution chain.");

        command.Subcommands.Add(CreateGetCommand(outputOption));
        command.Subcommands.Add(CreateSetCommand(outputOption));
        command.Subcommands.Add(CreateClearCommand(outputOption));
        return command;
    }

    // ---- get ---------------------------------------------------------------

    private static Command CreateGetCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var command = new Command(
            "get",
            "Print the unit's persisted execution defaults. All-null fields indicate " +
            "the unit has no declared default and member agents must supply their own.");
        command.Arguments.Add(unitArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var defaults = await client.GetUnitExecutionAsync(unitId, ct);

            // ADR-0038: { image, containerRuntime, runtime, model: {provider, id} }.
            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    image = defaults.Image,
                    container_runtime = defaults.ContainerRuntime,
                    runtime = defaults.Runtime,
                    model_provider = defaults.Model?.AiModelDto?.Provider,
                    model = defaults.Model?.AiModelDto?.Id,
                }));
                return;
            }

            Console.WriteLine($"Unit:     {unitId}");
            Console.WriteLine($"  image:             {defaults.Image ?? "(unset)"}");
            Console.WriteLine($"  container_runtime: {defaults.ContainerRuntime ?? "(unset)"}");
            Console.WriteLine($"  runtime:           {defaults.Runtime ?? "(unset)"}");
            Console.WriteLine($"  model_provider:    {defaults.Model?.AiModelDto?.Provider ?? "(unset)"}");
            Console.WriteLine($"  model:             {defaults.Model?.AiModelDto?.Id ?? "(unset)"}");
        });

        return command;
    }

    // ---- set ---------------------------------------------------------------

    private static Command CreateSetCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var imageOption = new Option<string?>("--image")
        {
            Description = "Default container image reference (e.g. ghcr.io/... or localhost/spring-voyage-agent-claude-code:latest).",
        };
        var runtimeOption = new Option<string?>("--runtime")
        {
            Description = "Default container runtime. Allowed values: " + string.Join(", ", RuntimeKeys) + ".",
        };
        runtimeOption.AcceptOnlyFromAmong(RuntimeKeys);

        var agentOption = new Option<string?>("--agent")
        {
            Description = "Default agent runtime registry id (e.g. claude, openai, google, ollama). " +
                "Drives launcher selection at dispatch via IAgentRuntime.Kind.",
        };

        var providerOption = new Option<string?>("--provider")
        {
            Description = "Default LLM provider (Spring Voyage Agent–specific; e.g. ollama, openai, anthropic, googleai).",
        };
        var modelOption = new Option<string?>("--model")
        {
            Description =
                "Default model identifier. Meaningful for every tool kind that carries a known provider family " +
                "(claude-code-cli, spring-voyage); the value is accepted as opaque and validated at " +
                "unit activation.",
        };

        var command = new Command(
            "set",
            "Upsert one or more fields on the unit's execution defaults. Partial update — " +
            "pass only the flags you want to change; unlisted fields keep their current value.");
        command.Arguments.Add(unitArg);
        command.Options.Add(imageOption);
        command.Options.Add(runtimeOption);
        command.Options.Add(agentOption);
        command.Options.Add(providerOption);
        command.Options.Add(modelOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var image = parseResult.GetValue(imageOption);
            var runtime = parseResult.GetValue(runtimeOption);
            var agent = parseResult.GetValue(agentOption);
            var provider = parseResult.GetValue(providerOption);
            var model = parseResult.GetValue(modelOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (string.IsNullOrWhiteSpace(image) && string.IsNullOrWhiteSpace(runtime)
                && string.IsNullOrWhiteSpace(agent) && string.IsNullOrWhiteSpace(provider)
                && string.IsNullOrWhiteSpace(model))
            {
                await Console.Error.WriteLineAsync(
                    "Nothing to set. Pass at least one of --image, --runtime, --agent, --provider, --model. " +
                    "Use 'clear' to wipe the block or 'clear --field X' to clear one field.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();

            // ADR-0038: structured Model {provider, id}.
            var modelDto = (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(model))
                ? new UnitExecutionResponse.UnitExecutionResponse_model
                {
                    AiModelDto = new AiModelDto { Provider = provider, Id = model },
                }
                : null;

            var stored = await client.SetUnitExecutionAsync(unitId, new UnitExecutionResponse
            {
                Image = image,
                ContainerRuntime = runtime,
                Runtime = agent,
                Model = modelDto,
            }, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    image = stored.Image,
                    container_runtime = stored.ContainerRuntime,
                    runtime = stored.Runtime,
                    model_provider = stored.Model?.AiModelDto?.Provider,
                    model = stored.Model?.AiModelDto?.Id,
                }));
            }
            else
            {
                Console.WriteLine($"Unit '{unitId}' execution updated.");
                Console.WriteLine($"  image:             {stored.Image ?? "(unset)"}");
                Console.WriteLine($"  container_runtime: {stored.ContainerRuntime ?? "(unset)"}");
                Console.WriteLine($"  runtime:           {stored.Runtime ?? "(unset)"}");
                Console.WriteLine($"  model_provider:    {stored.Model?.AiModelDto?.Provider ?? "(unset)"}");
                Console.WriteLine($"  model:             {stored.Model?.AiModelDto?.Id ?? "(unset)"}");
            }
        });

        return command;
    }

    // ---- clear -------------------------------------------------------------

    private static Command CreateClearCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var fieldOption = new Option<string?>("--field")
        {
            Description = "Clear one field only. Allowed values: " + string.Join(", ", FieldKeys) + ". " +
                "When omitted, the entire execution block is stripped.",
        };
        fieldOption.AcceptOnlyFromAmong(FieldKeys);

        var command = new Command(
            "clear",
            "Remove the unit's execution defaults. Without --field the entire block is " +
            "stripped; with --field only that slot is cleared (the others keep their value).");
        command.Arguments.Add(unitArg);
        command.Options.Add(fieldOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var field = parseResult.GetValue(fieldOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            if (string.IsNullOrWhiteSpace(field))
            {
                // Wipe the whole block.
                await client.ClearUnitExecutionAsync(unitId, ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                    {
                        unit = unitId,
                        image = (string?)null,
                        runtime = (string?)null,
                        agent = (string?)null,
                        provider = (string?)null,
                        model = (string?)null,
                    }));
                }
                else
                {
                    Console.WriteLine($"Unit '{unitId}' execution block cleared.");
                }
                return;
            }

            // Per-field clear: read current shape, rewrite every slot,
            // omitting the one the caller asked to clear. The server's
            // partial-update semantics require we resubmit the others so
            // the store doesn't drop them silently (a null field on PUT
            // means "leave alone", not "clear"). When every remaining
            // slot is null after this operation we fall through to the
            // full block-clear.
            var current = await client.GetUnitExecutionAsync(unitId, ct);
            // ADR-0038: per-field clear maps to the new wire fields.
            var keepImage = !string.Equals(field, "image", StringComparison.OrdinalIgnoreCase);
            var keepContainerRuntime = !string.Equals(field, "container_runtime", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(field, "runtime", StringComparison.OrdinalIgnoreCase);
            var keepRuntime = !string.Equals(field, "runtime", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(field, "agent", StringComparison.OrdinalIgnoreCase);
            var keepModel = !string.Equals(field, "model", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(field, "provider", StringComparison.OrdinalIgnoreCase);

            var updated = new UnitExecutionResponse
            {
                Image = keepImage ? current.Image : null,
                ContainerRuntime = keepContainerRuntime ? current.ContainerRuntime : null,
                Runtime = keepRuntime ? current.Runtime : null,
                Model = keepModel ? current.Model : null,
            };

            if (string.IsNullOrWhiteSpace(updated.Image) && string.IsNullOrWhiteSpace(updated.ContainerRuntime)
                && string.IsNullOrWhiteSpace(updated.Runtime) && updated.Model is null)
            {
                // Remaining state after per-field clear is empty → strip
                // the block entirely so we don't persist an empty object.
                await client.ClearUnitExecutionAsync(unitId, ct);
            }
            else
            {
                // Clear by DELETEing and re-PUTing — the store's partial
                // update cannot distinguish "leave alone" from "clear"
                // for a single field.
                await client.ClearUnitExecutionAsync(unitId, ct);
                await client.SetUnitExecutionAsync(unitId, updated, ct);
            }

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    image = updated.Image,
                    container_runtime = updated.ContainerRuntime,
                    runtime = updated.Runtime,
                    model_provider = updated.Model?.AiModelDto?.Provider,
                    model = updated.Model?.AiModelDto?.Id,
                }));
            }
            else
            {
                Console.WriteLine($"Unit '{unitId}' execution.{field} cleared.");
            }
        });

        return command;
    }
}