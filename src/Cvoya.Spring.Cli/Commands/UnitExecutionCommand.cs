// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring unit execution get|set|clear</c> verb subtree
/// (#601 / #603 / #409 B-wide; ADR-0038). Direct read/write access to
/// the manifest-persisted unit <c>execution:</c> block (image / runtime
/// / model-provider / model) without needing a full
/// <c>spring apply -f unit.yaml</c> re-apply.
/// </summary>
/// <remarks>
/// <para>
/// Each field is independently settable and independently clearable.
/// <c>set</c> performs a partial update — pass only the flags you want
/// to change. <c>clear</c> strips the whole block; <c>clear --field X</c>
/// clears one field only.
/// </para>
/// <para>
/// ADR-0038 reshapes the model surface: <c>--model-provider</c>
/// + <c>--model</c> ship the structured <c>execution.model = {provider, id}</c>
/// pair on the wire. <c>--model-provider</c> is required only when the
/// chosen runtime accepts more than one provider (e.g. <c>spring-voyage</c>);
/// for fixed-provider runtimes (<c>claude-code</c> → anthropic,
/// <c>codex</c> → openai, <c>gemini</c> → google) the flag is optional
/// and the dispatcher rejects mismatches.
/// </para>
/// <para>
/// ADR-0039 §7 removes the <c>--container-runtime</c> flag and the
/// matching <c>container-runtime</c> field key — the container runtime
/// is platform configuration, picked once by the host process at deploy
/// time. The flag is rejected at parse time with a hint per ADR-0039 §9.
/// </para>
/// </remarks>
public static class UnitExecutionCommand
{
    /// <summary>Field keys accepted on <c>clear --field</c>.</summary>
    /// <remarks>
    /// ADR-0038: the field-key surface is (image, runtime, model-provider, model).
    /// ADR-0039 §7: <c>container-runtime</c> is removed.
    /// </remarks>
    internal static readonly string[] FieldKeys =
    {
        "image", "runtime", "model-provider", "model",
    };

    /// <summary>
    /// Stderr message used by the legacy <c>--container-runtime</c>
    /// flag's parser-level rejection. Pinned by tests so a future flag
    /// rename doesn't slip past CI. Verbatim from ADR-0039 §9.
    /// </summary>
    public const string LegacyContainerRuntimeFlagRejectionMessage =
        "--container-runtime was removed in ADR-0039. " +
        "containerRuntime is removed in ADR-0039; the container runtime is platform configuration.";

    /// <summary>
    /// Entry point. Returns the <c>execution</c> subcommand tree for
    /// attachment under <c>unit</c>.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var command = new Command(
            "execution",
            "Read / write the unit's manifest-persisted execution defaults. " +
            "Fields: image, container-runtime, runtime, model-provider, model. Inherited by member " +
            "agents that don't declare their own value per the agent → unit → fail resolution chain.");

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

            // ADR-0038: { image, runtime, model: {provider, id} }.
            // ADR-0039 §7: containerRuntime slot removed.
            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    image = defaults.Image,
                    runtime = defaults.Runtime,
                    model_provider = defaults.Model?.AiModelDto?.Provider,
                    model = defaults.Model?.AiModelDto?.Id,
                }));
                return;
            }

            Console.WriteLine($"Unit:     {unitId}");
            Console.WriteLine($"  image:             {defaults.Image ?? "(unset)"}");
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
            Description = "Default container image reference (e.g. ghcr.io/cvoya-com/claude-code-base:latest).",
        };

        // ADR-0039 §7: legacy `--container-runtime` rejected at parse time.
        var legacyContainerRuntimeOption = new Option<string?>("--container-runtime")
        {
            Description = "REJECTED — the container runtime is platform configuration (ADR-0039 §7).",
            Hidden = true,
        };
        legacyContainerRuntimeOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0)
            {
                result.AddError(LegacyContainerRuntimeFlagRejectionMessage);
            }
        });

        var runtimeOption = new Option<string?>("--runtime")
        {
            Description = "Default agent runtime id (e.g. claude-code, codex, gemini, spring-voyage). " +
                "Drives launcher selection at dispatch.",
        };

        var modelProviderOption = new Option<string?>("--model-provider")
        {
            Description = "Default model-provider id (e.g. anthropic, openai, google, ollama). " +
                "Required for multi-provider runtimes (spring-voyage); optional otherwise (must match the runtime's implied provider when supplied).",
        };
        var modelOption = new Option<string?>("--model")
        {
            Description =
                "Default model id. The value is accepted as opaque on the wire and validated at unit activation.",
        };

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
        // ADR-0038: the flat `--provider` flag is gone — provider lives
        // inside the structured execution.model and is named via --model-provider.
        var legacyProviderOption = new Option<string?>("--provider")
        {
            Description = "REJECTED — use --model-provider instead (ADR-0038).",
            Hidden = true,
        };
        legacyProviderOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0)
            {
                result.AddError(LegacyProviderFlagRejectionMessage);
            }
        });

        var command = new Command(
            "set",
            "Upsert one or more fields on the unit's execution defaults. Partial update — " +
            "pass only the flags you want to change; unlisted fields keep their current value.");
        command.Arguments.Add(unitArg);
        command.Options.Add(imageOption);
        command.Options.Add(legacyContainerRuntimeOption);
        command.Options.Add(runtimeOption);
        command.Options.Add(modelProviderOption);
        command.Options.Add(modelOption);
        command.Options.Add(legacyAgentOption);
        command.Options.Add(legacyProviderOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var image = parseResult.GetValue(imageOption);
            var runtime = parseResult.GetValue(runtimeOption);
            var modelProvider = parseResult.GetValue(modelProviderOption);
            var model = parseResult.GetValue(modelOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (string.IsNullOrWhiteSpace(image)
                && string.IsNullOrWhiteSpace(runtime) && string.IsNullOrWhiteSpace(modelProvider)
                && string.IsNullOrWhiteSpace(model))
            {
                await Console.Error.WriteLineAsync(
                    "Nothing to set. Pass at least one of --image, --runtime, --model-provider, --model. " +
                    "Use 'clear' to wipe the block or 'clear --field X' to clear one field.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();

            // ADR-0038: structured execution.model = {provider, id}.
            var modelDto = (!string.IsNullOrWhiteSpace(modelProvider) && !string.IsNullOrWhiteSpace(model))
                ? new UnitExecutionResponse.UnitExecutionResponse_model
                {
                    AiModelDto = new AiModelDto { Provider = modelProvider, Id = model },
                }
                : null;

            var stored = await client.SetUnitExecutionAsync(unitId, new UnitExecutionResponse
            {
                Image = image,
                Runtime = runtime,
                Model = modelDto,
            }, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    image = stored.Image,
                    runtime = stored.Runtime,
                    model_provider = stored.Model?.AiModelDto?.Provider,
                    model = stored.Model?.AiModelDto?.Id,
                }));
            }
            else
            {
                Console.WriteLine($"Unit '{unitId}' execution updated.");
                Console.WriteLine($"  image:             {stored.Image ?? "(unset)"}");
                Console.WriteLine($"  runtime:           {stored.Runtime ?? "(unset)"}");
                Console.WriteLine($"  model_provider:    {stored.Model?.AiModelDto?.Provider ?? "(unset)"}");
                Console.WriteLine($"  model:             {stored.Model?.AiModelDto?.Id ?? "(unset)"}");
            }
        });

        return command;
    }

    /// <summary>
    /// Stderr message used by the legacy <c>--provider</c> flag's
    /// parser-level rejection. Pinned by tests so a future flag rename
    /// doesn't slip past CI.
    /// </summary>
    public const string LegacyProviderFlagRejectionMessage =
        "--provider was removed in ADR-0038. Provider now lives inside the structured " +
        "execution.model field — pass --model-provider <id> alongside --model <id>.";

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
            // ADR-0038: per-field clear targets one of
            // { image | runtime | model-provider | model }. ADR-0039 §7
            // removed the `container-runtime` key.
            // model-provider clears just the provider half of the structured
            // model; clearing model wipes the whole {provider, id} pair.
            var keepImage = !string.Equals(field, "image", StringComparison.OrdinalIgnoreCase);
            var keepRuntime = !string.Equals(field, "runtime", StringComparison.OrdinalIgnoreCase);
            var keepModel = !string.Equals(field, "model", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(field, "model-provider", StringComparison.OrdinalIgnoreCase);

            var updated = new UnitExecutionResponse
            {
                Image = keepImage ? current.Image : null,
                Runtime = keepRuntime ? current.Runtime : null,
                Model = keepModel ? current.Model : null,
            };

            if (string.IsNullOrWhiteSpace(updated.Image)
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
