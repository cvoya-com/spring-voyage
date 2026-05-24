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
/// / model-provider / model / system-prompt-mode) without needing a
/// full <c>spring apply -f unit.yaml</c> re-apply.
/// </summary>
/// <remarks>
/// <para>
/// Each field is independently settable and independently clearable.
/// <c>set</c> performs a partial update — pass only the flags you want
/// to change. <c>clear</c> strips the whole block; <c>clear --field X</c>
/// clears one field only. <c>clear --system-prompt-mode</c> clears the
/// prompt-shaping slot specifically (kept as its own flag rather than
/// another <c>--field</c> value for symmetry with the agent surface,
/// where the slot lives on a different endpoint).
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
    /// #2693: <c>system-prompt-mode</c> stays out of this list on purpose —
    /// the CLI exposes it as a dedicated boolean flag on <c>clear</c> for
    /// symmetry with the agent surface, where the slot rides a separate
    /// endpoint (PR E / #2714).
    /// </remarks>
    internal static readonly string[] FieldKeys =
    {
        "image", "runtime", "model-provider", "model",
    };

    /// <summary>
    /// Allowed values for <c>--system-prompt-mode</c> (#2693 / #2692).
    /// Wire form is the lower-case enum literal; the server validates the
    /// same set on the PUT boundary so we mirror it here to fail fast
    /// before the round-trip.
    /// </summary>
    internal static readonly string[] SystemPromptModeKeys = { "append", "replace" };

    /// <summary>
    /// Entry point. Returns the <c>execution</c> subcommand tree for
    /// attachment under <c>unit</c>.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var command = new Command(
            "execution",
            "Read / write the unit's manifest-persisted execution defaults. " +
            "Fields: image, runtime, model-provider, model, system-prompt-mode. Inherited by " +
            "member agents that don't declare their own value per the agent → unit → fail " +
            "resolution chain (system-prompt-mode further falls back to the platform default " +
            "'append' when no unit value is declared).");

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
            "the unit has no declared default and member agents must supply their own. " +
            "For system-prompt-mode both the declared (unit-own) and effective " +
            "(declared, else platform default 'append') values are printed.");
        command.Arguments.Add(unitArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(unitArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);
            string unitId;
            try
            {
                unitId = await resolver.ResolveUnitIdAsync(idOrName, parentContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            var defaults = await client.GetUnitExecutionAsync(unitId, ct);
            // #2693: for the unit the cascade has no level above it on the
            // system_prompt_mode axis — the effective value is the declared
            // value, falling back to the platform default 'append' when
            // unset. We compute that locally rather than fetching a
            // separate "effective" surface from the API.
            var declaredSpm = defaults.SystemPromptMode;
            var effectiveSpm = string.IsNullOrWhiteSpace(declaredSpm) ? "append" : declaredSpm;

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
                    declared_system_prompt_mode = declaredSpm,
                    system_prompt_mode = effectiveSpm,
                }));
                return;
            }

            Console.WriteLine($"Unit:     {idOrName}");
            Console.WriteLine($"  image:                          {defaults.Image ?? "(unset)"}");
            Console.WriteLine($"  runtime:                        {defaults.Runtime ?? "(unset)"}");
            Console.WriteLine($"  model_provider:                 {defaults.Model?.AiModelDto?.Provider ?? "(unset)"}");
            Console.WriteLine($"  model:                          {defaults.Model?.AiModelDto?.Id ?? "(unset)"}");
            Console.WriteLine($"  system_prompt_mode (declared):  {declaredSpm ?? "(unset)"}");
            Console.WriteLine($"  system_prompt_mode (effective): {effectiveSpm}");
        });

        return command;
    }

    // ---- set ---------------------------------------------------------------

    private static Command CreateSetCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var imageOption = new Option<string?>("--image")
        {
            Description = "Default container image reference (e.g. ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest).",
        };

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

        // #2693 / #2692: prompt-shaping default inherited by member agents
        // that don't declare their own system_prompt_mode.
        var systemPromptModeOption = new Option<string?>("--system-prompt-mode")
        {
            Description = "How the platform-assembled system prompt combines with the runtime's " +
                "own default. Allowed values: " + string.Join(", ", SystemPromptModeKeys) + ". " +
                "Inherited by member agents that don't declare their own value; falls back to " +
                "the platform default 'append' when unset.",
        };
        systemPromptModeOption.AcceptOnlyFromAmong(SystemPromptModeKeys);

        var command = new Command(
            "set",
            "Upsert one or more fields on the unit's execution defaults. Partial update — " +
            "pass only the flags you want to change; unlisted fields keep their current value.");
        command.Arguments.Add(unitArg);
        command.Options.Add(imageOption);
        command.Options.Add(runtimeOption);
        command.Options.Add(modelProviderOption);
        command.Options.Add(modelOption);
        command.Options.Add(systemPromptModeOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(unitArg)!;
            var image = parseResult.GetValue(imageOption);
            var runtime = parseResult.GetValue(runtimeOption);
            var modelProvider = parseResult.GetValue(modelProviderOption);
            var model = parseResult.GetValue(modelOption);
            var systemPromptMode = parseResult.GetValue(systemPromptModeOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (string.IsNullOrWhiteSpace(image)
                && string.IsNullOrWhiteSpace(runtime) && string.IsNullOrWhiteSpace(modelProvider)
                && string.IsNullOrWhiteSpace(model) && string.IsNullOrWhiteSpace(systemPromptMode))
            {
                await Console.Error.WriteLineAsync(
                    "Nothing to set. Pass at least one of --image, --runtime, --model-provider, " +
                    "--model, --system-prompt-mode. " +
                    "Use 'clear' to wipe the block or 'clear --field X' to clear one field.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);
            string unitId;
            try
            {
                unitId = await resolver.ResolveUnitIdAsync(idOrName, parentContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            // ADR-0038: structured execution.model = {provider, id}.
            var modelDto = (!string.IsNullOrWhiteSpace(modelProvider) && !string.IsNullOrWhiteSpace(model))
                ? new UnitExecutionResponse.UnitExecutionResponse_model
                {
                    AiModelDto = new AiModelDto { Provider = modelProvider, Id = model },
                }
                : null;

            // #2693: send the supplied systemPromptMode through the same
            // PUT — the server's partial-update semantics leave every
            // unsupplied slot alone.
            var stored = await client.SetUnitExecutionAsync(unitId, new UnitExecutionResponse
            {
                Image = image,
                Runtime = runtime,
                Model = modelDto,
                SystemPromptMode = systemPromptMode,
            }, ct);

            var declaredSpm = stored.SystemPromptMode;
            var effectiveSpm = string.IsNullOrWhiteSpace(declaredSpm) ? "append" : declaredSpm;

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    unit = unitId,
                    image = stored.Image,
                    runtime = stored.Runtime,
                    model_provider = stored.Model?.AiModelDto?.Provider,
                    model = stored.Model?.AiModelDto?.Id,
                    declared_system_prompt_mode = declaredSpm,
                    system_prompt_mode = effectiveSpm,
                }));
            }
            else
            {
                Console.WriteLine($"Unit '{idOrName}' execution updated.");
                Console.WriteLine($"  image:                          {stored.Image ?? "(unset)"}");
                Console.WriteLine($"  runtime:                        {stored.Runtime ?? "(unset)"}");
                Console.WriteLine($"  model_provider:                 {stored.Model?.AiModelDto?.Provider ?? "(unset)"}");
                Console.WriteLine($"  model:                          {stored.Model?.AiModelDto?.Id ?? "(unset)"}");
                Console.WriteLine($"  system_prompt_mode (declared):  {declaredSpm ?? "(unset)"}");
                Console.WriteLine($"  system_prompt_mode (effective): {effectiveSpm}");
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
            Description = "Clear one execution-block field only. Allowed values: " + string.Join(", ", FieldKeys) + ". " +
                "When omitted (and --system-prompt-mode is not set) the entire execution block is stripped.",
        };
        fieldOption.AcceptOnlyFromAmong(FieldKeys);

        // #2693: dedicated boolean flag to clear the prompt-shaping slot.
        // The server's PUT path treats null as "leave alone" (matches the
        // other slots), so per-field clear is implemented as
        // DELETE-then-re-PUT below — same pattern as the existing --field
        // clearer. Surfaces the slot as its own flag for symmetry with
        // the agent CLI, where it lives on a different endpoint entirely.
        var systemPromptModeOption = new Option<bool>("--system-prompt-mode")
        {
            Description = "Clear the unit's declared system-prompt-mode. Member agents that " +
                "inherit from the unit will fall back to the platform default 'append' once " +
                "the slot is unset. Independent of --field; do not combine the two.",
        };

        var command = new Command(
            "clear",
            "Remove the unit's execution defaults. Without --field or --system-prompt-mode the " +
            "entire block is stripped; with --field only that slot is cleared; with " +
            "--system-prompt-mode only the prompt-shaping slot is cleared. The two flags are " +
            "mutually exclusive.");
        command.Arguments.Add(unitArg);
        command.Options.Add(fieldOption);
        command.Options.Add(systemPromptModeOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(unitArg)!;
            var field = parseResult.GetValue(fieldOption);
            var clearSystemPromptMode = parseResult.GetValue(systemPromptModeOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

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
            string unitId;
            try
            {
                unitId = await resolver.ResolveUnitIdAsync(idOrName, parentContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            if (clearSystemPromptMode)
            {
                // Treat as a per-field clear targeting `system-prompt-mode`.
                // The server's PUT path cannot distinguish "leave alone"
                // from "clear" for an individual null slot, so we fall
                // through to the DELETE-then-re-PUT pattern below.
                await ClearOneFieldAsync(client, unitId, idOrName, slotKey: "system-prompt-mode", output, ct);
                return;
            }

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
                        system_prompt_mode = (string?)null,
                    }));
                }
                else
                {
                    Console.WriteLine($"Unit '{idOrName}' execution block cleared.");
                }
                return;
            }

            await ClearOneFieldAsync(client, unitId, idOrName, slotKey: field, output, ct);
        });

        return command;
    }

    /// <summary>
    /// Implements the per-slot clear pattern: read current shape, strip
    /// the target slot, then DELETE-and-re-PUT so the server persists the
    /// reduced state. When every remaining slot is empty we leave the
    /// block stripped rather than re-PUTting an empty body (which the
    /// server rejects).
    /// </summary>
    /// <remarks>
    /// <paramref name="slotKey"/> is one of <c>image</c>, <c>runtime</c>,
    /// <c>model</c>, <c>model-provider</c>, or (since #2693)
    /// <c>system-prompt-mode</c>. <c>model-provider</c> clears just the
    /// provider half of the structured <c>execution.model</c>; clearing
    /// <c>model</c> wipes the whole <c>{provider, id}</c> pair.
    /// </remarks>
    private static async Task ClearOneFieldAsync(
        SpringApiClient client,
        string unitId,
        string idOrName,
        string slotKey,
        string output,
        CancellationToken ct)
    {
        var current = await client.GetUnitExecutionAsync(unitId, ct);
        var keepImage = !string.Equals(slotKey, "image", StringComparison.OrdinalIgnoreCase);
        var keepRuntime = !string.Equals(slotKey, "runtime", StringComparison.OrdinalIgnoreCase);
        var keepModel = !string.Equals(slotKey, "model", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(slotKey, "model-provider", StringComparison.OrdinalIgnoreCase);
        var keepSystemPromptMode = !string.Equals(slotKey, "system-prompt-mode", StringComparison.OrdinalIgnoreCase);

        var updated = new UnitExecutionResponse
        {
            Image = keepImage ? current.Image : null,
            Runtime = keepRuntime ? current.Runtime : null,
            Model = keepModel ? current.Model : null,
            SystemPromptMode = keepSystemPromptMode ? current.SystemPromptMode : null,
        };

        if (string.IsNullOrWhiteSpace(updated.Image)
            && string.IsNullOrWhiteSpace(updated.Runtime)
            && updated.Model is null
            && string.IsNullOrWhiteSpace(updated.SystemPromptMode))
        {
            // Remaining state after per-field clear is empty → strip the
            // block entirely so we don't persist an empty object.
            await client.ClearUnitExecutionAsync(unitId, ct);
        }
        else
        {
            // Clear by DELETEing and re-PUTing — the store's partial
            // update cannot distinguish "leave alone" from "clear" for a
            // single field.
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
                system_prompt_mode = updated.SystemPromptMode,
            }));
        }
        else
        {
            // Render the human-readable key in the same form the user typed.
            var human = string.Equals(slotKey, "system-prompt-mode", StringComparison.OrdinalIgnoreCase)
                ? "system_prompt_mode"
                : slotKey;
            Console.WriteLine($"Unit '{idOrName}' execution.{human} cleared.");
        }
    }
}
