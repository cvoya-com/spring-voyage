// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

using Microsoft.Kiota.Abstractions;

/// <summary>
/// Builds the "unit" command tree for unit management.
/// </summary>
public static class UnitCommand
{
    private static readonly OutputFormatter.Column<UnitResponse>[] UnitColumns =
    {
        new("id", u => u.Id),
        new("name", u => u.Name),
    };

    private static readonly OutputFormatter.Column<UnitMembershipResponse>[] MembershipColumns =
    {
        new("unit", m => m.UnitId),
        new("agent", m => m.AgentAddress),
        new("model", m => m.Model),
        new("specialty", m => m.Specialty),
        new("enabled", m => m.Enabled?.ToString().ToLowerInvariant()),
        new("executionMode", m => m.ExecutionMode?.AgentExecutionMode?.ToString()),
    };

    /// <summary>
    /// Unified member-list row emitted by <c>unit members list</c> (#352). Agent-
    /// scheme rows carry per-membership config overrides; unit-scheme rows leave
    /// those fields null because sub-unit memberships have no per-child config
    /// today (deferred to #217). The explicit <c>Scheme</c> column lets scripts
    /// filter with <c>jq '.[] | select(.scheme == "unit")'</c> without having to
    /// reason about address-prefix conventions.
    /// </summary>
    private sealed record MemberListRow(
        string Scheme,
        string Member,
        string Unit,
        string? Model,
        string? Specialty,
        bool? Enabled,
        string? ExecutionMode);

    private static readonly OutputFormatter.Column<MemberListRow>[] MemberListColumns =
    {
        new("scheme", r => r.Scheme),
        new("member", r => r.Member),
        new("unit", r => r.Unit),
        new("model", r => r.Model),
        new("specialty", r => r.Specialty),
        new("enabled", r => r.Enabled?.ToString().ToLowerInvariant()),
        new("executionMode", r => r.ExecutionMode),
    };

    /// <summary>
    /// Creates the "unit" command with subcommands for CRUD, member operations,
    /// and the cascading purge helper.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var unitCommand = new Command("unit", "Manage units");

        unitCommand.Subcommands.Add(CreateListCommand(outputOption));
        unitCommand.Subcommands.Add(CreateCreateCommand(outputOption));
        // #460 — `create-from-template` promoted to a first-class verb; the
        // `--from-template` flag on `create` keeps working but now prints a
        // deprecation notice.
        unitCommand.Subcommands.Add(CreateCreateFromTemplateCommand(outputOption));
        unitCommand.Subcommands.Add(CreateDeleteCommand());
        unitCommand.Subcommands.Add(CreatePurgeCommand());
        unitCommand.Subcommands.Add(CreateStartCommand());
        unitCommand.Subcommands.Add(CreateStopCommand());
        // T-08 / #950: `revalidate <name>` re-runs the backend validation
        // workflow for a unit in Error/Stopped. Default behaviour is wait-
        // until-terminal (same poll loop as `create`); `--no-wait` returns
        // immediately after the 202.
        unitCommand.Subcommands.Add(CreateRevalidateCommand());
        unitCommand.Subcommands.Add(CreateStatusCommand(outputOption));
        unitCommand.Subcommands.Add(CreateMembersCommand(outputOption));
        // #454 — humans add/remove/list.
        unitCommand.Subcommands.Add(UnitHumansCommand.Create(outputOption));
        // #453 — policy <dimension> get/set/clear across the five UnitPolicy
        // dimensions.
        unitCommand.Subcommands.Add(UnitPolicyCommand.Create(outputOption));
        // #412 — expertise get/set/aggregated.
        unitCommand.Subcommands.Add(ExpertiseCommand.CreateUnitSubcommand(outputOption));
        // #413 — boundary get/set/clear (opacity, projection, synthesis).
        unitCommand.Subcommands.Add(UnitBoundaryCommand.Create(outputOption));
        // #606 — orchestration get/set/clear for the manifest-persisted
        // strategy slot (direct read/write surface deferred by ADR-0010).
        unitCommand.Subcommands.Add(UnitOrchestrationCommand.Create(outputOption));
        // #601 / #603 / #409 B-wide — execution get/set/clear for the
        // unit's execution defaults (image / runtime / tool / provider /
        // model) inherited by member agents.
        unitCommand.Subcommands.Add(UnitExecutionCommand.Create(outputOption));

        return unitCommand;
    }

    private static Command CreateListCommand(Option<string> outputOption)
    {
        var command = new Command("list", "List all units");

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.ListUnitsAsync(ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, UnitColumns));
        });

        return command;
    }

    private static Command CreateCreateCommand(Option<string> outputOption)
    {
        // "name" is the unit's address path and unique identifier; the server
        // generates the actor id. ZeroOrOne so `--from-template <package>/<name>`
        // (#316) can supply the unit name via `--name` instead of the positional —
        // the template-derived path otherwise inherits the manifest name, which
        // collides across repeated instantiations (#325). Note the positional
        // stays supported for the direct-create path so existing callers
        // (`spring unit create eng-team`) keep working verbatim.
        var nameArg = new Argument<string?>("name")
        {
            Description = "The unit name (address path; also used as the identifier). Optional when --from-template and --name are supplied.",
            Arity = System.CommandLine.ArgumentArity.ZeroOrOne,
        };
        var displayNameOption = new Option<string?>("--display-name") { Description = "Human-readable display name (defaults to name)" };
        var descriptionOption = new Option<string?>("--description") { Description = "Description of the unit's purpose" };
        // #315: model/color ride on the same CreateUnitRequest. Kept as plain
        // strings — no hex validation here so the server remains the source
        // of truth on shape.
        var modelOption = new Option<string?>("--model")
        {
            Description =
                "Optional LLM model identifier (e.g. claude-sonnet-4-6). " +
                "Accepted as opaque for every tool that carries a known provider " +
                "(claude-code / codex / gemini / dapr-agent); validation happens at unit activation.",
        };
        var colorOption = new Option<string?>("--color")
        {
            Description = "Optional UI accent colour hint (e.g. #6366f1).",
        };
        // #316: alternative "instantiate this template" path. Format is
        // <package>/<template-name>; the server resolves both halves from the
        // packages catalog. Present only on this command — `apply -f` stays
        // on the direct manifest-parsing path so the two subcommands map
        // 1:1 onto the two server endpoints.
        var fromTemplateOption = new Option<string?>("--from-template")
        {
            Description = "Instantiate from a packaged template. Format: <package>/<template-name>.",
        };
        // #316 + #325: explicit unit name override for the template path.
        // The positional 'name' stays the preferred entry on the direct-create
        // path; --name is the spelling when --from-template is present (the
        // positional would otherwise read ambiguously against the template
        // basename). Either surfaces the same override on the request body.
        var unitNameOption = new Option<string?>("--name")
        {
            Description = "Override the unit name when using --from-template. Required when no positional name is supplied.",
        };
        // #350: execution tool, provider, and hosting mode.
        var toolOption = new Option<string?>("--tool")
        {
            Description = "Execution tool (claude-code, codex, gemini, dapr-agent, custom).",
        };
        toolOption.AcceptOnlyFromAmong("claude-code", "codex", "gemini", "dapr-agent", "custom");
        var providerOption = new Option<string?>("--provider")
        {
            Description = "LLM provider (ollama, openai, google, anthropic). Relevant when --tool is dapr-agent.",
        };
        providerOption.AcceptOnlyFromAmong("ollama", "openai", "google", "anthropic", "claude");
        var hostingOption = new Option<string?>("--hosting")
        {
            Description = "Agent hosting mode (ephemeral, persistent).",
        };
        hostingOption.AcceptOnlyFromAmong("ephemeral", "persistent");

        // #626: inline credential entry. Pair these flags with --provider /
        // --tool to supply the LLM API key at unit-create time. See
        // `UnitCredentialOptions` for the full rejection matrix.
        var apiKeyOption = new Option<string?>("--api-key")
        {
            Description =
                "LLM API key for the derived provider (set inline). Rejected when the tool / provider has no key (ollama, custom). Mutually exclusive with --api-key-from-file.",
        };
        var apiKeyFromFileOption = new Option<string?>("--api-key-from-file")
        {
            Description =
                "Path to a file containing the LLM API key. Trailing newlines are stripped. Mutually exclusive with --api-key.",
        };
        var saveAsTenantDefaultOption = new Option<bool>("--save-as-tenant-default")
        {
            Description =
                "Pair with --api-key / --api-key-from-file to write the key as a tenant-default secret instead of a unit-scoped secret.",
        };

        // Review feedback on #744: every unit must have a parent. Either
        // one or more --parent-unit ids (parent = another unit) or the
        // explicit --top-level flag (parent = tenant) is required.
        // Neither / both is rejected at parse time so callers see the
        // error before the server returns 400. Repeatable so a unit can
        // attach to multiple parents in one call.
        var parentUnitOption = new Option<string[]>("--parent-unit")
        {
            Description = "Parent unit to attach the new unit to. Repeat for multiple parents. "
                + "Mutually exclusive with --top-level; exactly one of the two forms is required.",
            AllowMultipleArgumentsPerToken = true,
        };
        var topLevelOption = new Option<bool>("--top-level")
        {
            Description = "Mark the new unit as a top-level unit (parent = tenant). "
                + "Mutually exclusive with --parent-unit.",
        };

        // T-08 / #950: backend validation is now the authoritative gate. The
        // CLI defaults to wait-until-terminal (polling GET once per second)
        // so operators see pass/fail inline; `--no-wait` returns as soon as
        // the server has accepted the create and is in `Validating`.
        var noWaitOption = new Option<bool>("--no-wait")
        {
            Description = "Do not wait for backend validation to finish. Return as soon as the server "
                + "accepts the create and reports Validating (or Draft for partial configs).",
        };

        var command = new Command(
            "create",
            "Create a new unit.\n\n"
            + "By default waits for backend validation to finish (polls GET /api/v1/units/{name} "
            + "once per second until the unit reaches Stopped or Error). Pass --no-wait to return "
            + "immediately after the create is accepted. Progress in the CLI is coarse — a single "
            + "\"Validating...\" indicator until terminal; the web portal renders per-step progress "
            + "via the SSE channel.\n\n"
            + UnitValidationExitCodes.HelpTable);
        command.Arguments.Add(nameArg);
        command.Options.Add(displayNameOption);
        command.Options.Add(descriptionOption);
        command.Options.Add(modelOption);
        command.Options.Add(colorOption);
        command.Options.Add(fromTemplateOption);
        command.Options.Add(unitNameOption);
        command.Options.Add(toolOption);
        command.Options.Add(providerOption);
        command.Options.Add(hostingOption);
        command.Options.Add(apiKeyOption);
        command.Options.Add(apiKeyFromFileOption);
        command.Options.Add(saveAsTenantDefaultOption);
        command.Options.Add(parentUnitOption);
        command.Options.Add(topLevelOption);
        command.Options.Add(noWaitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var positionalName = parseResult.GetValue(nameArg);
            var displayName = parseResult.GetValue(displayNameOption);
            var description = parseResult.GetValue(descriptionOption);
            var model = parseResult.GetValue(modelOption);
            var color = parseResult.GetValue(colorOption);
            var fromTemplate = parseResult.GetValue(fromTemplateOption);
            var unitNameOverride = parseResult.GetValue(unitNameOption);
            var tool = parseResult.GetValue(toolOption);
            var provider = parseResult.GetValue(providerOption);
            var hosting = parseResult.GetValue(hostingOption);
            var apiKey = parseResult.GetValue(apiKeyOption);
            var apiKeyFromFile = parseResult.GetValue(apiKeyFromFileOption);
            var saveAsTenantDefault = parseResult.GetValue(saveAsTenantDefaultOption);
            var parentUnits = (parseResult.GetValue(parentUnitOption) ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .ToArray();
            var topLevel = parseResult.GetValue(topLevelOption);
            var noWait = parseResult.GetValue(noWaitOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            // Review feedback on #744: reject neither / both at parse time
            // so callers see a local error instead of the server's 400.
            if (topLevel && parentUnits.Length > 0)
            {
                await Console.Error.WriteLineAsync(
                    "--top-level and --parent-unit are mutually exclusive. Supply exactly one.");
                Environment.Exit(1);
                return;
            }
            if (!topLevel && parentUnits.Length == 0)
            {
                await Console.Error.WriteLineAsync(
                    "Every unit must have a parent. Supply one or more --parent-unit <id> flags, "
                    + "or pass --top-level to attach the unit directly to the tenant.");
                Environment.Exit(1);
                return;
            }

            // #598 + #644: reject --provider on non-dapr-agent tools
            // (their provider is baked in), and reject both flags on
            // --tool=custom (no declared contract). --model is accepted
            // for every tool that carries a known provider family so
            // operators can pick within that family.
            var providerModelError = ValidateProviderModelAgainstTool(tool, provider, model);
            if (providerModelError is not null)
            {
                await Console.Error.WriteLineAsync(providerModelError);
                Environment.Exit(1);
                return;
            }

            // #626: resolve + validate --api-key / --api-key-from-file /
            // --save-as-tenant-default. Rejection surfaces here so callers
            // find out before we POST the unit-create request.
            // #742: the canonical secret name now comes from the agent-
            // runtime API response rather than a client-side switch, so
            // the CLI, portal, and resolver stay in lock-step off the
            // same authority.
            var credentialClient = ClientFactory.Create();
            var credentialResolution = await ResolveCredentialOptionsAsync(
                tool,
                provider,
                apiKey,
                apiKeyFromFile,
                saveAsTenantDefault,
                RuntimeSecretNameResolver(credentialClient),
                ct);
            if (credentialResolution.ErrorMessage is not null)
            {
                await Console.Error.WriteLineAsync(credentialResolution.ErrorMessage);
                Environment.Exit(1);
                return;
            }

            if (!string.IsNullOrWhiteSpace(fromTemplate))
            {
                // --from-template path: positional 'name' is reinterpreted as
                // the override when --name is absent. This keeps the shell
                // ergonomics close to the direct-create form while the flag
                // spelling stays explicit for scripts.
                //
                // #460: this flag is deprecated in favour of the first-class
                // `spring unit create-from-template` verb. The flag keeps
                // working verbatim so existing scripts are not broken; we
                // just nudge operators towards the new spelling in stderr
                // so docs / onboarding mat
                // erial can move off the flag.
                await Console.Error.WriteLineAsync(
                    "warning: `spring unit create --from-template` is deprecated. " +
                    "Use `spring unit create-from-template <package>/<template> [--name ...]` instead.");

                var effectiveUnitName = !string.IsNullOrWhiteSpace(unitNameOverride)
                    ? unitNameOverride
                    : positionalName;

                var exitCode = await ExecuteCreateFromTemplateAsync(
                    fromTemplate!,
                    effectiveUnitName,
                    displayName,
                    model,
                    color,
                    tool,
                    provider,
                    hosting,
                    output,
                    credentialResolution,
                    ct,
                    parentUnitIds: parentUnits.Length > 0 ? parentUnits : null,
                    isTopLevel: topLevel);
                if (exitCode != 0)
                {
                    Environment.Exit(exitCode);
                }
                return;
            }

            // Direct-create path: positional 'name' is required.
            if (string.IsNullOrWhiteSpace(positionalName))
            {
                await Console.Error.WriteLineAsync(
                    "Missing unit name. Supply it as the first argument, or use --from-template <package>/<name> to instantiate a template.");
                Environment.Exit(1);
                return;
            }

            var directClient = credentialClient;

            // #626: when --save-as-tenant-default is set, write the
            // tenant secret BEFORE the unit is created so a failure
            // there doesn't leave an orphan actor behind.
            if (credentialResolution is { Key.Length: > 0, SaveAsTenantDefault: true, SecretName: not null })
            {
                try
                {
                    await directClient.CreateTenantSecretAsync(
                        credentialResolution.SecretName,
                        credentialResolution.Key,
                        externalStoreKey: null,
                        ct);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"Failed to write tenant default '{credentialResolution.SecretName}': {ex.Message}");
                    Environment.Exit(1);
                    return;
                }
            }

            var result = await directClient.CreateUnitAsync(
                positionalName!,
                displayName,
                description,
                model: model,
                color: color,
                tool: tool,
                provider: provider,
                hosting: hosting,
                parentUnitIds: parentUnits.Length > 0 ? parentUnits : null,
                isTopLevel: topLevel ? true : null,
                ct: ct);

            // #626: when --save-as-tenant-default is NOT set, write the
            // unit-scoped override after the unit exists.
            if (credentialResolution is { Key.Length: > 0, SaveAsTenantDefault: false, SecretName: not null })
            {
                try
                {
                    await directClient.CreateUnitSecretAsync(
                        result.Name!,
                        credentialResolution.SecretName,
                        credentialResolution.Key,
                        externalStoreKey: null,
                        ct);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"warning: unit secret '{credentialResolution.SecretName}' not written: {ex.Message}");
                }
            }

            // JSON mode stays script-compatible — scripts parsing stdout
            // with jq don't want the human-facing wait-loop lines. We still
            // honour --no-wait / the default wait in JSON mode by printing
            // the JSON envelope once and returning; progress updates would
            // break the "one JSON object per CLI call" contract.
            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(result));
                return;
            }

            Console.WriteLine(OutputFormatter.FormatTable(result, UnitColumns));

            // T-08 / #950: default is wait-until-terminal; --no-wait opts
            // out. Snapshot the POST response then either print the hint or
            // hand off to the shared polling loop.
            var createdName = result.Name;
            if (string.IsNullOrWhiteSpace(createdName))
            {
                // The server guarantees a name on 201, but be defensive —
                // without it we can't poll, and falling back to exit 1
                // beats an ArgumentException deep inside the loop.
                return;
            }

            if (noWait)
            {
                Console.WriteLine(RenderNoWaitHint(createdName!, result.Status));
                return;
            }

            var waitExitCode = await RunUnitValidationWaitAsync(directClient, createdName!, result, ct);
            if (waitExitCode != 0)
            {
                Environment.Exit(waitExitCode);
            }
        });

        return command;
    }

    /// <summary>
    /// T-08 / #950: new <c>spring unit revalidate &lt;name&gt;</c> verb.
    /// Posts <c>POST /api/v1/units/{name}/revalidate</c>, surfaces 409 as a
    /// usage error (exit 2) with the server's current-status message, and
    /// otherwise reuses the shared wait loop so the UX matches
    /// <c>spring unit create</c>.
    /// </summary>
    private static Command CreateRevalidateCommand()
    {
        var nameArg = new Argument<string>("name")
        {
            Description = "The unit name to revalidate. Must currently be in Error or Stopped.",
        };
        var noWaitOption = new Option<bool>("--no-wait")
        {
            Description = "Do not wait for backend validation to finish. Return as soon as the server "
                + "accepts the request (HTTP 202) and flips the unit back to Validating.",
        };

        var command = new Command(
            "revalidate",
            "Re-run backend validation for a unit currently in Error or Stopped.\n\n"
            + "By default waits for validation to finish (polls GET /api/v1/units/{name} once per "
            + "second until the unit reaches Stopped or Error). Pass --no-wait to return immediately "
            + "after the 202 Accepted. Progress in the CLI is coarse — a single \"Validating...\" "
            + "indicator until terminal; the web portal renders per-step progress via the SSE channel. "
            + "Rejected with exit code 2 when the unit is in any other state (Running, Starting, ...).\n\n"
            + UnitValidationExitCodes.HelpTable);
        command.Arguments.Add(nameArg);
        command.Options.Add(noWaitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var noWait = parseResult.GetValue(noWaitOption);
            var client = ClientFactory.Create();

            UnitResponse accepted;
            try
            {
                accepted = await client.RevalidateUnitAsync(name, ct);
            }
            catch (ApiException ex) when (ex.ResponseStatusCode == 409)
            {
                // 409 is a contract-level rejection: the unit is in a
                // status that doesn't support revalidate (Running,
                // Starting, Validating, etc.). Exit 2 (usage error) per
                // the T-08 code table.
                await Console.Error.WriteLineAsync(
                    $"Cannot revalidate unit '{name}': {ExtractServerDetail(ex)}");
                Environment.Exit(UnitValidationExitCodes.UsageError);
                return;
            }
            catch (ApiException ex)
            {
                await Console.Error.WriteLineAsync($"Failed to revalidate unit '{name}': {ex.Message}");
                Environment.Exit(UnitValidationExitCodes.UnknownError);
                return;
            }

            if (noWait)
            {
                Console.WriteLine(
                    $"Unit '{name}' revalidation accepted. Status: {accepted.Status}. "
                    + "Use 'spring unit get <name>' to check progress.");
                return;
            }

            var exitCode = await RunUnitValidationWaitAsync(client, name, accepted, ct);
            if (exitCode != 0)
            {
                Environment.Exit(exitCode);
            }
        });

        return command;
    }

    /// <summary>
    /// Shared wait-loop wiring for <c>create</c> and <c>revalidate</c>.
    /// Converts the initial Kiota <see cref="UnitResponse"/> into a
    /// <see cref="UnitValidationSnapshot"/>, then pumps the loop via
    /// <c>SpringApiClient.GetUnitAsync</c> with the default 1-second poll
    /// interval. The actual loop logic lives in
    /// <see cref="UnitValidationWaitLoop"/>, which is testable in isolation
    /// from the HTTP plumbing.
    /// </summary>
    private static Task<int> RunUnitValidationWaitAsync(
        SpringApiClient client,
        string unitName,
        UnitResponse initial,
        CancellationToken ct)
    {
        async Task<UnitValidationSnapshot> Fetch(CancellationToken token)
        {
            var detail = await client.GetUnitAsync(unitName, token);
            return ToSnapshot(detail.Unit);
        }

        return UnitValidationWaitLoop.RunAsync(
            unitName,
            ToSnapshot(initial),
            Fetch,
            Console.Out,
            Console.Error,
            ct);
    }

    /// <summary>
    /// Produces a <see cref="UnitValidationSnapshot"/> from a Kiota
    /// <see cref="UnitResponse"/>, unwrapping the composed-type wrapper
    /// around <c>lastValidationError</c>. Safe against null / partial
    /// payloads — missing fields surface as null on the snapshot.
    /// </summary>
    internal static UnitValidationSnapshot ToSnapshot(UnitResponse? response)
    {
        if (response is null)
        {
            return new UnitValidationSnapshot(
                Status: "Unknown",
                ValidationRunId: null,
                ErrorCode: null,
                ErrorStep: null,
                ErrorMessage: null,
                ErrorDetails: null);
        }

        var status = response.Status?.ToString() ?? "Unknown";
        var inner = response.LastValidationError?.UnitValidationError;
        IReadOnlyDictionary<string, string>? details = null;
        if (inner?.Details?.AdditionalData is { Count: > 0 } data)
        {
            var map = new Dictionary<string, string>(data.Count, StringComparer.Ordinal);
            foreach (var (key, value) in data)
            {
                map[key] = value?.ToString() ?? string.Empty;
            }
            details = map;
        }

        return new UnitValidationSnapshot(
            Status: status,
            ValidationRunId: response.LastValidationRunId,
            ErrorCode: inner?.Code,
            ErrorStep: inner?.Step?.ToString(),
            ErrorMessage: inner?.Message,
            ErrorDetails: details);
    }

    /// <summary>
    /// Renders the <c>--no-wait</c> hint line that replaces the wait
    /// loop's terminal output on <c>spring unit create --no-wait</c>.
    /// The server may return either <c>Validating</c> (full config — the
    /// workflow is running) or <c>Draft</c> (partial config — nothing to
    /// validate yet); we echo whichever came back so operators don't have
    /// to re-run <c>unit get</c> just to learn which path they got.
    /// </summary>
    internal static string RenderNoWaitHint(string unitName, UnitStatus? status)
    {
        var statusString = status?.ToString() ?? "Unknown";
        return $"Unit '{unitName}' created. Status: {statusString}. "
            + "Use 'spring unit get <name>' to check progress.";
    }

    /// <summary>
    /// Best-effort extraction of the server's problem-detail message from a
    /// Kiota <see cref="ApiException"/>. Kiota doesn't offer a strongly-
    /// typed reader without a generated schema for every error body, so
    /// we fall back to the exception's message (which includes the body
    /// text) when present.
    /// </summary>
    internal static string ExtractServerDetail(ApiException ex)
    {
        var message = ex.Message;
        return string.IsNullOrWhiteSpace(message)
            ? "server rejected the request."
            : message;
    }

    /// <summary>
    /// First-class <c>spring unit create-from-template &lt;package&gt;/&lt;template-name&gt;</c>
    /// verb (#460). Mirrors the legacy <c>--from-template</c> flag on
    /// <c>create</c> but surfaces template instantiation as a distinct verb
    /// so <c>create</c>'s argument tree stays about "direct create" shape and
    /// help output is easier to read. Shares the same HTTP surface
    /// (<c>POST /api/v1/units/from-template</c>) via a common executor so the
    /// two entry points never drift.
    /// </summary>
    private static Command CreateCreateFromTemplateCommand(Option<string> outputOption)
    {
        var targetArg = new Argument<string>("target")
        {
            Description = "Template reference in the form <package>/<template-name>.",
        };
        var unitNameOption = new Option<string?>("--name")
        {
            Description =
                "Override the unit name. Defaults to the manifest-derived name; pass this " +
                "when instantiating the same template multiple times so the address paths don't collide.",
        };
        var displayNameOption = new Option<string?>("--display-name")
        {
            Description = "Human-readable display name (falls back to the template's default).",
        };
        // Alias --display alongside --display-name for shell ergonomics. The
        // tracking issue spells the flag as --display; --display-name is the
        // existing convention on `spring unit create`, so we accept both.
        displayNameOption.Aliases.Add("--display");
        var modelOption = new Option<string?>("--model")
        {
            Description =
                "Optional LLM model identifier override (e.g. claude-sonnet-4-6). " +
                "Accepted as opaque for every tool that carries a known provider " +
                "(claude-code / codex / gemini / dapr-agent); validation happens at unit activation.",
        };
        var colorOption = new Option<string?>("--color")
        {
            Description = "Optional UI accent colour hint (e.g. #6366f1).",
        };
        var toolOption = new Option<string?>("--tool")
        {
            Description = "Execution tool (claude-code, codex, gemini, dapr-agent, custom).",
        };
        toolOption.AcceptOnlyFromAmong("claude-code", "codex", "gemini", "dapr-agent", "custom");
        var providerOption = new Option<string?>("--provider")
        {
            Description = "LLM provider (ollama, openai, google, anthropic, claude).",
        };
        providerOption.AcceptOnlyFromAmong("ollama", "openai", "google", "anthropic", "claude");
        var hostingOption = new Option<string?>("--hosting")
        {
            Description = "Agent hosting mode (ephemeral, persistent).",
        };
        hostingOption.AcceptOnlyFromAmong("ephemeral", "persistent");

        // #626: inline credential entry (same semantics as `unit create`).
        var apiKeyOption = new Option<string?>("--api-key")
        {
            Description =
                "LLM API key for the derived provider (set inline). Rejected when the tool / provider has no key (ollama, custom). Mutually exclusive with --api-key-from-file.",
        };
        var apiKeyFromFileOption = new Option<string?>("--api-key-from-file")
        {
            Description =
                "Path to a file containing the LLM API key. Trailing newlines are stripped. Mutually exclusive with --api-key.",
        };
        var saveAsTenantDefaultOption = new Option<bool>("--save-as-tenant-default")
        {
            Description =
                "Pair with --api-key / --api-key-from-file to write the key as a tenant-default secret instead of a unit-scoped secret.",
        };

        // Review feedback on #744: parent-required flags (repeated on the
        // template path so the two entry points stay in lock-step).
        var parentUnitOption = new Option<string[]>("--parent-unit")
        {
            Description = "Parent unit to attach the new unit to. Repeat for multiple parents. "
                + "Mutually exclusive with --top-level; exactly one of the two forms is required.",
            AllowMultipleArgumentsPerToken = true,
        };
        var topLevelOption = new Option<bool>("--top-level")
        {
            Description = "Mark the new unit as a top-level unit (parent = tenant). "
                + "Mutually exclusive with --parent-unit.",
        };

        var command = new Command(
            "create-from-template",
            "Instantiate a unit from a packaged template. First-class verb equivalent to the " +
            "deprecated `spring unit create --from-template <package>/<template>` flag.");
        command.Arguments.Add(targetArg);
        command.Options.Add(unitNameOption);
        command.Options.Add(displayNameOption);
        command.Options.Add(modelOption);
        command.Options.Add(colorOption);
        command.Options.Add(toolOption);
        command.Options.Add(providerOption);
        command.Options.Add(hostingOption);
        command.Options.Add(apiKeyOption);
        command.Options.Add(apiKeyFromFileOption);
        command.Options.Add(saveAsTenantDefaultOption);
        command.Options.Add(parentUnitOption);
        command.Options.Add(topLevelOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var target = parseResult.GetValue(targetArg)!;
            var unitName = parseResult.GetValue(unitNameOption);
            var displayName = parseResult.GetValue(displayNameOption);
            var model = parseResult.GetValue(modelOption);
            var color = parseResult.GetValue(colorOption);
            var tool = parseResult.GetValue(toolOption);
            var provider = parseResult.GetValue(providerOption);
            var hosting = parseResult.GetValue(hostingOption);
            var apiKey = parseResult.GetValue(apiKeyOption);
            var apiKeyFromFile = parseResult.GetValue(apiKeyFromFileOption);
            var saveAsTenantDefault = parseResult.GetValue(saveAsTenantDefaultOption);
            var parentUnits = (parseResult.GetValue(parentUnitOption) ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .ToArray();
            var topLevel = parseResult.GetValue(topLevelOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            // Review feedback on #744: reject neither / both at parse time
            // so callers see a local error instead of the server's 400.
            if (topLevel && parentUnits.Length > 0)
            {
                await Console.Error.WriteLineAsync(
                    "--top-level and --parent-unit are mutually exclusive. Supply exactly one.");
                Environment.Exit(1);
                return;
            }
            if (!topLevel && parentUnits.Length == 0)
            {
                await Console.Error.WriteLineAsync(
                    "Every unit must have a parent. Supply one or more --parent-unit <id> flags, "
                    + "or pass --top-level to attach the unit directly to the tenant.");
                Environment.Exit(1);
                return;
            }

            // #598 + #644: same gate applies to the template path —
            // --provider rejected for non-dapr-agent, both rejected for
            // custom, --model accepted for every known-provider tool.
            var providerModelError = ValidateProviderModelAgainstTool(tool, provider, model);
            if (providerModelError is not null)
            {
                await Console.Error.WriteLineAsync(providerModelError);
                Environment.Exit(1);
                return;
            }

            // #742: secret name comes from the agent-runtime payload —
            // see RuntimeSecretNameResolver below.
            var credentialClient = ClientFactory.Create();
            var credentialResolution = await ResolveCredentialOptionsAsync(
                tool,
                provider,
                apiKey,
                apiKeyFromFile,
                saveAsTenantDefault,
                RuntimeSecretNameResolver(credentialClient),
                ct);
            if (credentialResolution.ErrorMessage is not null)
            {
                await Console.Error.WriteLineAsync(credentialResolution.ErrorMessage);
                Environment.Exit(1);
                return;
            }

            var exitCode = await ExecuteCreateFromTemplateAsync(
                target,
                unitName,
                displayName,
                model,
                color,
                tool,
                provider,
                hosting,
                output,
                credentialResolution,
                ct,
                parentUnitIds: parentUnits.Length > 0 ? parentUnits : null,
                isTopLevel: topLevel);
            if (exitCode != 0)
            {
                Environment.Exit(exitCode);
            }
        });

        return command;
    }

    /// <summary>
    /// Executes <c>POST /api/v1/units/from-template</c> with the supplied
    /// inputs and renders the response in the requested output format.
    /// Shared by both the legacy <c>--from-template</c> flag on
    /// <c>create</c> and the new first-class <c>create-from-template</c>
    /// verb so the two paths cannot drift on warnings / rendering / error
    /// handling.
    /// </summary>
    private static async Task<int> ExecuteCreateFromTemplateAsync(
        string target,
        string? unitName,
        string? displayName,
        string? model,
        string? color,
        string? tool,
        string? provider,
        string? hosting,
        string output,
        UnitCredentialOptions credential,
        CancellationToken ct,
        IReadOnlyList<string>? parentUnitIds = null,
        bool isTopLevel = false)
    {
        var slash = target.IndexOf('/');
        if (slash <= 0 || slash == target.Length - 1)
        {
            await Console.Error.WriteLineAsync(
                "Template reference must be in the form <package>/<template-name>.");
            return 1;
        }

        var package = target[..slash];
        var templateName = target[(slash + 1)..];

        var client = ClientFactory.Create();

        // #626: tenant-default secret is written BEFORE the unit exists.
        // Fails the whole command if the tenant write fails.
        if (credential is { Key.Length: > 0, SaveAsTenantDefault: true, SecretName: not null })
        {
            try
            {
                await client.CreateTenantSecretAsync(
                    credential.SecretName,
                    credential.Key,
                    externalStoreKey: null,
                    ct);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to write tenant default '{credential.SecretName}': {ex.Message}");
                return 1;
            }
        }

        var response = await client.CreateUnitFromTemplateAsync(
            package,
            templateName,
            unitName: unitName,
            displayName: displayName,
            model: model,
            color: color,
            tool: tool,
            provider: provider,
            hosting: hosting,
            parentUnitIds: parentUnitIds,
            isTopLevel: isTopLevel ? true : null,
            ct: ct);

        // #626: unit-scoped secret is written AFTER the unit exists.
        // Failure here surfaces as a warning — the unit is already live
        // and the operator can retry from the Secrets tab / CLI.
        var createdUnitName = response.Unit?.Name;
        if (credential is { Key.Length: > 0, SaveAsTenantDefault: false, SecretName: not null }
            && !string.IsNullOrWhiteSpace(createdUnitName))
        {
            try
            {
                await client.CreateUnitSecretAsync(
                    createdUnitName!,
                    credential.SecretName,
                    credential.Key,
                    externalStoreKey: null,
                    ct);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync(
                    $"warning: unit secret '{credential.SecretName}' not written: {ex.Message}");
            }
        }

        // Surface server-side warnings (unresolved bundle tools, binding
        // previews) on both output paths so callers never miss them.
        if (response.Warnings is { Count: > 0 } warnings)
        {
            foreach (var warning in warnings)
            {
                await Console.Error.WriteLineAsync($"warning: {warning}");
            }
        }

        if (output == "json")
        {
            Console.WriteLine(OutputFormatter.FormatJson(response));
        }
        else
        {
            var unit = response.Unit
                ?? throw new InvalidOperationException(
                    "Server returned a from-template response with no unit envelope.");
            Console.WriteLine(OutputFormatter.FormatTable(unit, UnitColumns));
        }
        return 0;
    }

    private static Command CreateDeleteCommand()
    {
        var idArg = new Argument<string>("id") { Description = "The unit identifier" };
        var command = new Command("delete", "Delete a unit");
        command.Arguments.Add(idArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var client = ClientFactory.Create();

            await client.DeleteUnitAsync(id, ct);
            Console.WriteLine($"Unit '{id}' deleted.");
        });

        return command;
    }

    private static Command CreatePurgeCommand()
    {
        var idArg = new Argument<string>("id") { Description = "The unit identifier" };
        var confirmOption = new Option<bool>("--confirm")
        {
            Description = "Required acknowledgement that this cascading delete is intentional",
        };
        var command = new Command(
            "purge",
            "Cascading cleanup: delete every membership row for the unit, then delete the unit itself. Requires --confirm because it is destructive.");
        command.Arguments.Add(idArg);
        command.Options.Add(confirmOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var confirm = parseResult.GetValue(confirmOption);
            if (!confirm)
            {
                await Console.Error.WriteLineAsync(
                    $"Refusing to purge unit '{id}' without --confirm. Re-run with --confirm to proceed.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();

            // Step 1: enumerate memberships so the user sees exactly what is cascading.
            var memberships = await client.ListUnitMembershipsAsync(id, ct);
            Console.WriteLine(
                $"Purging unit '{id}': {memberships.Count} membership(s) to remove before the unit itself.");

            // Step 2: delete each membership row. We fail loud on the first error so
            // the caller can investigate before the unit itself disappears.
            foreach (var membership in memberships)
            {
                var agentAddress = membership.AgentAddress ?? string.Empty;
                Console.WriteLine($"  - removing membership for agent '{agentAddress}'");
                await client.DeleteMembershipAsync(id, agentAddress, ct);
            }

            // Step 3: delete the unit.
            Console.WriteLine($"  - deleting unit '{id}'");
            await client.DeleteUnitAsync(id, ct);
            Console.WriteLine($"Unit '{id}' purged.");
        });

        return command;
    }

    private static Command CreateStartCommand()
    {
        var nameArg = new Argument<string>("name") { Description = "The unit name" };
        var command = new Command("start", "Start a unit (transitions Draft->Starting or Stopped->Starting)");
        command.Arguments.Add(nameArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var client = ClientFactory.Create();

            try
            {
                var result = await client.StartUnitAsync(name, ct);
                Console.WriteLine($"Unit '{name}' is now {result.Status}.");
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync($"Failed to start unit '{name}': {ex.Message}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateStopCommand()
    {
        var nameArg = new Argument<string>("name") { Description = "The unit name" };
        var command = new Command("stop", "Stop a running unit");
        command.Arguments.Add(nameArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var client = ClientFactory.Create();

            try
            {
                var result = await client.StopUnitAsync(name, ct);
                Console.WriteLine($"Unit '{name}' is now {result.Status}.");
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync($"Failed to stop unit '{name}': {ex.Message}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateStatusCommand(Option<string> outputOption)
    {
        var nameArg = new Argument<string>("name") { Description = "The unit name" };
        var command = new Command("status", "Show the current status and readiness of a unit");
        command.Arguments.Add(nameArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var name = parseResult.GetValue(nameArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            try
            {
                var unitTask = client.GetUnitAsync(name, ct);
                var readinessTask = client.GetUnitReadinessAsync(name, ct);
                await Task.WhenAll(unitTask, readinessTask);

                var unit = unitTask.Result;
                var readiness = readinessTask.Result;

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                    {
                        name,
                        status = unit.Unit?.Status?.ToString(),
                        isReady = readiness.IsReady,
                        missingRequirements = readiness.MissingRequirements,
                    }));
                }
                else
                {
                    Console.WriteLine($"Unit:     {name}");
                    Console.WriteLine($"Status:   {unit.Unit?.Status}");
                    Console.WriteLine($"Ready:    {(readiness.IsReady == true ? "yes" : "no")}");
                    if (readiness.MissingRequirements is { Count: > 0 } missing)
                    {
                        Console.WriteLine($"Missing:  {string.Join(", ", missing)}");
                    }
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync($"Failed to get status for unit '{name}': {ex.Message}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateMembersCommand(Option<string> outputOption)
    {
        var membersCommand = new Command("members", "Manage unit memberships (agents assigned to this unit)");

        membersCommand.Subcommands.Add(CreateMembersListCommand(outputOption));
        membersCommand.Subcommands.Add(CreateMembersAddCommand(outputOption));
        membersCommand.Subcommands.Add(CreateMembersConfigCommand(outputOption));
        membersCommand.Subcommands.Add(CreateMembersRemoveCommand());

        return membersCommand;
    }

    private static Command CreateMembersListCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var command = new Command(
            "list",
            "List every member of this unit (agents AND sub-units), with per-membership config overrides for agent-scheme rows.");
        command.Arguments.Add(unitArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            // Two sources — unified here because neither alone gives the full
            // picture today:
            //  - `GET /units/{id}/members` returns every member (agents AND
            //    sub-units) from the unit actor's member list.
            //  - `GET /units/{id}/memberships` holds only agent-scheme rows
            //    with per-membership config overrides.
            //
            // We join them so callers see both kinds in one command. The
            // `scheme` column lets scripts filter (`jq '.[] | select(.scheme
            // == "unit")'`) and the table output clearly distinguishes the
            // two kinds even at a glance.
            var membersTask = client.ListUnitMembersAsync(unitId, ct);
            var membershipsTask = client.ListUnitMembershipsAsync(unitId, ct);
            await Task.WhenAll(membersTask, membershipsTask);

            var members = membersTask.Result;
            var memberships = membershipsTask.Result;

            // Index agent-scheme overrides by address so we can enrich the
            // authoritative member list with per-membership config that lives
            // in `unit_memberships`.
            var overrides = memberships
                .Where(m => !string.IsNullOrEmpty(m.AgentAddress))
                .ToDictionary(m => m.AgentAddress!, StringComparer.Ordinal);

            var rows = new List<MemberListRow>();
            var seenAgents = new HashSet<string>(StringComparer.Ordinal);

            foreach (var addr in members)
            {
                var scheme = addr.Scheme ?? "agent";
                var path = addr.Path ?? string.Empty;

                if (string.Equals(scheme, "agent", StringComparison.Ordinal)
                    && overrides.TryGetValue(path, out var m))
                {
                    rows.Add(new MemberListRow(
                        Scheme: "agent",
                        Member: path,
                        Unit: m.UnitId ?? unitId,
                        Model: m.Model,
                        Specialty: m.Specialty,
                        Enabled: m.Enabled,
                        ExecutionMode: m.ExecutionMode?.AgentExecutionMode?.ToString()));
                    seenAgents.Add(path);
                }
                else
                {
                    rows.Add(new MemberListRow(
                        Scheme: scheme,
                        Member: path,
                        Unit: unitId,
                        Model: null,
                        Specialty: null,
                        Enabled: null,
                        ExecutionMode: null));
                    if (string.Equals(scheme, "agent", StringComparison.Ordinal))
                    {
                        seenAgents.Add(path);
                    }
                }
            }

            // Defensive fall-back: if the /members call returned an empty
            // list (actor unreachable), surface the agent-scheme rows from
            // the repository anyway so the command doesn't appear broken.
            foreach (var m in memberships)
            {
                var address = m.AgentAddress;
                if (string.IsNullOrEmpty(address) || seenAgents.Contains(address))
                {
                    continue;
                }
                rows.Add(new MemberListRow(
                    Scheme: "agent",
                    Member: address,
                    Unit: m.UnitId ?? unitId,
                    Model: m.Model,
                    Specialty: m.Specialty,
                    Enabled: m.Enabled,
                    ExecutionMode: m.ExecutionMode?.AgentExecutionMode?.ToString()));
            }

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJsonPlain(rows)
                : OutputFormatter.FormatTable(rows, MemberListColumns));
        });

        return command;
    }


    private static Command CreateMembersAddCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var (options, bind, agentOption, unitOption) = BuildAddMembershipOptions();
        var command = new Command(
            "add",
            "Add an agent (--agent) or a sub-unit (--unit) as a member of this unit. Exactly one of --agent or --unit must be supplied.");
        command.Arguments.Add(unitArg);
        foreach (var option in options)
        {
            command.Options.Add(option);
        }

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var parentUnitId = parseResult.GetValue(unitArg)!;
            var agentId = parseResult.GetValue(agentOption);
            var childUnitId = parseResult.GetValue(unitOption);

            var hasAgent = !string.IsNullOrWhiteSpace(agentId);
            var hasChildUnit = !string.IsNullOrWhiteSpace(childUnitId);

            if (hasAgent == hasChildUnit)
            {
                await Console.Error.WriteLineAsync(hasAgent
                    ? "--agent and --unit are mutually exclusive. Supply exactly one."
                    : "One of --agent or --unit is required.");
                Environment.Exit(1);
                return;
            }

            if (hasChildUnit)
            {
                // Per-membership overrides are agent-only today (#217). Reject
                // them early with a clear message so the caller isn't left
                // wondering why their --model silently disappeared.
                if (HasAnyAgentOnlyOverride(parseResult, options))
                {
                    await Console.Error.WriteLineAsync(
                        "--model, --specialty, --enabled and --execution-mode apply to --agent members only. Remove them when using --unit.");
                    Environment.Exit(1);
                    return;
                }

                var client = ClientFactory.Create();
                try
                {
                    await client.AddUnitMemberAsync(parentUnitId, childUnitId!, ct);
                }
                catch (Microsoft.Kiota.Abstractions.ApiException ex)
                {
                    // The server returns 409 with a cycle-path payload when the
                    // proposed edge would close a cycle. Surface the server's
                    // message verbatim so operators see the offending chain
                    // rather than a generic Kiota error.
                    await Console.Error.WriteLineAsync(
                        $"Failed to add unit '{childUnitId}' as a member of '{parentUnitId}': {ex.Message}");
                    Environment.Exit(1);
                    return;
                }

                Console.WriteLine($"Unit '{childUnitId}' added as a member of '{parentUnitId}'.");
                return;
            }

            // Agent path: reuse the existing membership-upsert flow so
            // per-membership overrides (model/specialty/enabled/executionMode)
            // remain first-class on this surface.
            await InvokeUpsertAsync(parseResult, unitArg, bind, outputOption, ct);
        });

        return command;
    }

    /// <summary>
    /// Returns true when any of the agent-only per-membership overrides
    /// (<c>--model</c>, <c>--specialty</c>, <c>--enabled</c>, <c>--execution-mode</c>)
    /// has been supplied on the current parse. Used by the <c>--unit</c> branch
    /// of <c>members add</c> to reject mixed flag sets up-front (#331).
    /// </summary>
    private static bool HasAnyAgentOnlyOverride(ParseResult parseResult, Option[] options)
    {
        foreach (var option in options)
        {
            var name = option.Name;
            if (name is "--model" or "--specialty" or "--enabled" or "--execution-mode")
            {
                var result = parseResult.GetResult(option);
                if (result is not null && !result.Implicit)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private static Command CreateMembersConfigCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var (options, bind) = BuildMembershipOptions();
        var command = new Command(
            "config",
            "Update per-membership config for an existing agent in this unit. Same underlying upsert as 'add', but semantically signals a configuration change rather than a new assignment.");
        command.Arguments.Add(unitArg);
        foreach (var option in options)
        {
            command.Options.Add(option);
        }

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
            await InvokeUpsertAsync(parseResult, unitArg, bind, outputOption, ct));

        return command;
    }

    private static Command CreateMembersRemoveCommand()
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var agentOption = new Option<string>("--agent")
        {
            Description = "The agent identifier to remove from this unit",
            Required = true,
        };
        var command = new Command("remove", "Remove an agent's membership from this unit.");
        command.Arguments.Add(unitArg);
        command.Options.Add(agentOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var agentId = parseResult.GetValue(agentOption)!;
            var client = ClientFactory.Create();

            await client.DeleteMembershipAsync(unitId, agentId, ct);
            Console.WriteLine($"Membership for agent '{agentId}' removed from unit '{unitId}'.");
        });

        return command;
    }

    /// <summary>
    /// Shared options + parse helper for the agent-only upsert path
    /// (<c>members config</c>; <c>members add</c> when <c>--agent</c> is used).
    /// <c>--agent</c> is declared <see cref="Option.Required"/> so the parser
    /// enforces presence on <c>config</c>. <see cref="BuildAddMembershipOptions"/>
    /// relaxes that for <c>add</c> where <c>--unit</c> is an alternative (#331).
    /// </summary>
    private static (Option[] Options, Func<ParseResult, MembershipInputs> Bind) BuildMembershipOptions()
    {
        var agentOption = new Option<string?>("--agent")
        {
            Description = "The agent identifier",
            Required = true,
        };
        return BuildMembershipOptionsInternal(agentOption);
    }

    /// <summary>
    /// Variant used by <c>members add</c>: both <c>--agent</c> and <c>--unit</c>
    /// are declared non-required at the parser level because exactly one is
    /// valid. The action body enforces the mutual-exclusion rule with a clear
    /// error message when both / neither are supplied.
    /// </summary>
    private static (Option[] Options, Func<ParseResult, MembershipInputs> Bind, Option<string?> AgentOption, Option<string?> UnitOption)
        BuildAddMembershipOptions()
    {
        var agentOption = new Option<string?>("--agent")
        {
            Description = "The agent identifier (mutually exclusive with --unit).",
        };
        var unitOption = new Option<string?>("--unit")
        {
            Description = "The sub-unit identifier to add as a member (mutually exclusive with --agent).",
        };

        var (options, bind) = BuildMembershipOptionsInternal(agentOption);
        // --unit needs to be registered on the command too. Prepend so help
        // text shows it next to --agent.
        var merged = new Option[options.Length + 1];
        merged[0] = unitOption;
        Array.Copy(options, 0, merged, 1, options.Length);
        return (merged, bind, agentOption, unitOption);
    }

    private static (Option[] Options, Func<ParseResult, MembershipInputs> Bind) BuildMembershipOptionsInternal(
        Option<string?> agentOption)
    {
        var modelOption = new Option<string?>("--model") { Description = "Override the agent's default model for this unit" };
        var specialtyOption = new Option<string?>("--specialty") { Description = "Override the agent's specialty for this unit" };
        var enabledOption = new Option<bool?>("--enabled") { Description = "Enable/disable this membership (true or false)" };
        var executionModeOption = new Option<string?>("--execution-mode") { Description = "Override execution mode (Auto or OnDemand)" };
        executionModeOption.AcceptOnlyFromAmong("Auto", "OnDemand");

        MembershipInputs Bind(ParseResult pr)
        {
            var executionModeRaw = pr.GetValue(executionModeOption);
            AgentExecutionMode? executionMode = executionModeRaw switch
            {
                null => null,
                "Auto" => AgentExecutionMode.Auto,
                "OnDemand" => AgentExecutionMode.OnDemand,
                _ => throw new InvalidOperationException($"Unknown execution mode '{executionModeRaw}'."),
            };
            return new MembershipInputs(
                AgentId: pr.GetValue(agentOption) ?? string.Empty,
                Model: pr.GetValue(modelOption),
                Specialty: pr.GetValue(specialtyOption),
                Enabled: pr.GetValue(enabledOption),
                ExecutionMode: executionMode);
        }

        return (new Option[] { agentOption, modelOption, specialtyOption, enabledOption, executionModeOption }, Bind);
    }

    private static async Task InvokeUpsertAsync(
        ParseResult parseResult,
        Argument<string> unitArg,
        Func<ParseResult, MembershipInputs> bind,
        Option<string> outputOption,
        CancellationToken ct)
    {
        var unitId = parseResult.GetValue(unitArg)!;
        var inputs = bind(parseResult);
        var output = parseResult.GetValue(outputOption) ?? "table";
        var client = ClientFactory.Create();

        var result = await client.UpsertMembershipAsync(
            unitId,
            inputs.AgentId,
            inputs.Model,
            inputs.Specialty,
            inputs.Enabled,
            inputs.ExecutionMode,
            ct);

        Console.WriteLine(output == "json"
            ? OutputFormatter.FormatJson(result)
            : OutputFormatter.FormatTable(result, MembershipColumns));
    }

    private sealed record MembershipInputs(
        string AgentId,
        string? Model,
        string? Specialty,
        bool? Enabled,
        AgentExecutionMode? ExecutionMode);

    // Canonical rejection message (#644) — operators read this verbatim
    // when they combine --provider / --model with a tool that doesn't
    // accept that flag. The CLI and the portal mirror the same policy:
    // dapr-agent takes both, claude-code/codex/gemini take --model only,
    // custom takes neither.
    internal const string ProviderModelRejectionMessage =
        "--provider is only meaningful for --tool=dapr-agent; " +
        "other tools (claude-code, codex, gemini) have their provider hardcoded in the tool CLI, " +
        "but accept --model to pick within that provider's model family.";

    /// <summary>
    /// Shared validator used by <c>spring unit create</c> and
    /// <c>spring unit create-from-template</c>. Rejects <c>--provider</c>
    /// and <c>--model</c> on the tools that don't accept them (#598,
    /// #644). The matrix is:
    /// <list type="bullet">
    /// <item><description><c>dapr-agent</c> — both flags accepted.</description></item>
    /// <item><description><c>claude-code</c> / <c>codex</c> / <c>gemini</c> —
    /// provider is hardcoded in the tool's own CLI (rejected), but
    /// <c>--model</c> is accepted so operators can pick within the tool's
    /// baked-in provider family (Anthropic / OpenAI / Google). Value is
    /// treated as opaque; the server validates at unit activation.</description></item>
    /// <item><description><c>custom</c> — no declared contract, both
    /// rejected.</description></item>
    /// <item><description>tool unset — no second-guessing the server
    /// default, both flags accepted.</description></item>
    /// </list>
    /// See <c>docs/architecture/cli-and-web.md</c> and
    /// <c>docs/architecture/agent-runtime.md</c> for the full rationale.
    /// </summary>
    /// <param name="tool">Value of <c>--tool</c> (null when not supplied).</param>
    /// <param name="provider">Value of <c>--provider</c> (null when not supplied).</param>
    /// <param name="model">Value of <c>--model</c> (null when not supplied).</param>
    /// <returns>
    /// Null when the combination is valid. An error message suitable for
    /// stderr when the combination is rejected.
    /// </returns>
    public static string? ValidateProviderModelAgainstTool(
        string? tool,
        string? provider,
        string? model)
    {
        // No constraint when neither --provider nor --model was passed —
        // the server resolves defaults. When --tool is absent we also
        // skip the check: the server picks the default tool (claude-code
        // at the time of writing) and the CLI doesn't know the default
        // authoritatively, so rejecting `--provider` in that case would
        // be overreach. Operators who want to pin Provider / Model must
        // also name the tool they're targeting.
        var hasProvider = !string.IsNullOrWhiteSpace(provider);
        var hasModel = !string.IsNullOrWhiteSpace(model);
        if (!hasProvider && !hasModel)
        {
            return null;
        }

        var normalizedTool = (tool ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedTool.Length == 0)
        {
            return null;
        }

        // dapr-agent is the only tool that takes a user-chosen provider.
        // The other supported tools (claude-code, codex, gemini) have
        // their provider baked in but still accept --model. custom has
        // no declared contract so both flags are rejected.
        return normalizedTool switch
        {
            "dapr-agent" => null,
            "claude-code" or "codex" or "gemini" => hasProvider
                ? ProviderModelRejectionMessage
                : null,
            _ => ProviderModelRejectionMessage,
        };
    }

    /// <summary>
    /// #742: adapter over <see cref="SpringApiClient.GetAgentRuntimeAsync"/>
    /// that satisfies the
    /// <c>Func&lt;string, CancellationToken, Task&lt;string?&gt;&gt;</c>
    /// resolver signature expected by
    /// <see cref="ResolveCredentialOptionsAsync"/>. Returns the runtime's
    /// <c>CredentialSecretName</c> verbatim — <c>null</c> when the runtime
    /// is not installed on the current tenant, <see cref="string.Empty"/>
    /// when the runtime declares no credential (for example Ollama).
    /// </summary>
    private static Func<string, CancellationToken, Task<string?>> RuntimeSecretNameResolver(
        SpringApiClient client)
        => async (runtimeId, ct) =>
        {
            var runtime = await client.GetAgentRuntimeAsync(runtimeId, ct);
            return runtime?.CredentialSecretName;
        };

    /// <summary>
    /// #626 / #742: derive the agent-runtime id whose credential the
    /// operator's tool + provider combination needs, so the CLI can fetch
    /// <c>credentialSecretName</c> from <c>GET /api/v1/agent-runtimes/{id}</c>
    /// instead of hardcoding the provider → secret-name map. Returns
    /// <c>null</c> when the combination has no declared credential contract
    /// (<c>custom</c> tool, <c>--tool</c> omitted, or an unknown provider on
    /// <c>dapr-agent</c>).
    /// </summary>
    /// <remarks>
    /// Ollama maps to the <c>ollama</c> runtime id even though it needs no
    /// key — the runtime's own <c>CredentialSecretName</c> is the empty
    /// string, which <see cref="ResolveCredentialOptionsAsync"/> treats as
    /// "no credential to write". Mirrors the portal-side
    /// <c>deriveRequiredRuntimeId</c> in
    /// <c>src/Cvoya.Spring.Web/src/app/units/create/page.tsx</c>.
    /// </remarks>
    public static string? DeriveRequiredRuntimeId(
        string? tool,
        string? provider)
    {
        var normalizedTool = (tool ?? string.Empty).Trim().ToLowerInvariant();
        var normalizedProvider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        return normalizedTool switch
        {
            "claude-code" => "claude",
            "codex" => "openai",
            "gemini" => "google",
            "dapr-agent" => normalizedProvider switch
            {
                "claude" or "anthropic" => "claude",
                "openai" => "openai",
                "google" or "gemini" or "googleai" => "google",
                "ollama" => "ollama",
                _ => null,
            },
            // custom / unspecified → no declared credential contract.
            _ => null,
        };
    }

    /// <summary>
    /// #626 / #742: resolve the inline-credential flags into a validated
    /// payload. Handles mutual exclusion between <c>--api-key</c> and
    /// <c>--api-key-from-file</c>, rejects keys on tool/provider
    /// combinations that have no credential contract, fetches the
    /// canonical secret name from the runtime registry via
    /// <paramref name="runtimeSecretNameResolver"/>, and loads the file
    /// contents when the <c>--api-key-from-file</c> path is used.
    /// </summary>
    /// <param name="runtimeSecretNameResolver">
    /// Asks the platform for a given runtime id's <c>credentialSecretName</c>
    /// (the string the resolver returns flows straight into the tenant /
    /// unit secret write). <c>null</c> means "runtime not installed";
    /// <see cref="string.Empty"/> means "runtime declares no credential"
    /// (for example Ollama). The indirection keeps this method testable
    /// without an API round-trip.
    /// </param>
    /// <remarks>
    /// The secret-name mapping used to live in a client-side switch
    /// (<c>SecretNameForProvider</c>) that mirrored each runtime's
    /// <c>IAgentRuntime.CredentialSecretName</c>. #742 deleted the switch
    /// and routes the lookup through
    /// <c>GET /api/v1/agent-runtimes/{id}</c> so the CLI, portal, and
    /// resolver stay in lock-step off a single authority.
    /// </remarks>
    public static async Task<UnitCredentialOptions> ResolveCredentialOptionsAsync(
        string? tool,
        string? provider,
        string? apiKey,
        string? apiKeyFromFile,
        bool saveAsTenantDefault,
        Func<string, CancellationToken, Task<string?>> runtimeSecretNameResolver,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(runtimeSecretNameResolver);

        var hasKeyFlag = !string.IsNullOrEmpty(apiKey);
        var hasKeyFileFlag = !string.IsNullOrEmpty(apiKeyFromFile);

        // --save-as-tenant-default is only meaningful with a key.
        if (saveAsTenantDefault && !hasKeyFlag && !hasKeyFileFlag)
        {
            return UnitCredentialOptions.Rejected(
                "--save-as-tenant-default requires --api-key or --api-key-from-file.");
        }

        if (!hasKeyFlag && !hasKeyFileFlag)
        {
            return UnitCredentialOptions.None();
        }

        if (hasKeyFlag && hasKeyFileFlag)
        {
            return UnitCredentialOptions.Rejected(
                "--api-key and --api-key-from-file are mutually exclusive. Pass exactly one.");
        }

        var runtimeId = DeriveRequiredRuntimeId(tool, provider);
        if (runtimeId is null)
        {
            return UnitCredentialOptions.Rejected(
                "--api-key / --api-key-from-file is only valid for tools that map to a registered agent runtime " +
                "(claude-code, codex, gemini, or dapr-agent with a known provider). " +
                "custom tools have no declared credential contract.");
        }

        // Ollama's runtime id is known but its CredentialSecretName is
        // the empty string — that means "no credential to write", so the
        // inline-key flags have nowhere to land.
        var secretName = await runtimeSecretNameResolver(runtimeId, ct);
        if (secretName is null)
        {
            return UnitCredentialOptions.Rejected(
                $"Agent runtime '{runtimeId}' is not installed on the current tenant. " +
                "Install it (`spring agent-runtime install " + runtimeId + "`) before supplying an API key.");
        }
        if (secretName.Length == 0)
        {
            return UnitCredentialOptions.Rejected(
                $"Agent runtime '{runtimeId}' declares no credential (runs without an API key). " +
                "Drop --api-key / --api-key-from-file for this tool/provider combination.");
        }

        string? resolvedKey;
        if (hasKeyFlag)
        {
            resolvedKey = apiKey;
        }
        else
        {
            try
            {
                resolvedKey = await File.ReadAllTextAsync(apiKeyFromFile!, ct);
                resolvedKey = resolvedKey.TrimEnd('\r', '\n');
            }
            catch (Exception ex)
            {
                return UnitCredentialOptions.Rejected(
                    $"Failed to read --api-key-from-file '{apiKeyFromFile}': {ex.Message}");
            }
        }

        if (string.IsNullOrEmpty(resolvedKey))
        {
            return UnitCredentialOptions.Rejected(
                "Supplied API key is empty. Pass a non-empty value via --api-key or a file that contains one.");
        }

        return new UnitCredentialOptions(
            Key: resolvedKey,
            SecretName: secretName,
            SaveAsTenantDefault: saveAsTenantDefault,
            ErrorMessage: null);
    }
}

/// <summary>
/// #626: validated result of the <c>--api-key</c> /
/// <c>--api-key-from-file</c> / <c>--save-as-tenant-default</c> flag
/// triple. Produced by <see cref="UnitCommand.ResolveCredentialOptionsAsync"/>
/// and threaded through the unit-create executors so the tenant /
/// unit secret writes happen with the right scope at the right time.
/// </summary>
/// <param name="Key">
/// The resolved key value (from <c>--api-key</c> or the file named by
/// <c>--api-key-from-file</c>). Empty when no key was supplied — the
/// executors check <see cref="SecretName"/> for null to detect that.
/// </param>
/// <param name="SecretName">
/// The canonical secret name (<c>anthropic-api-key</c>,
/// <c>openai-api-key</c>, or <c>google-api-key</c>) derived from the
/// tool/provider. Null when no key was supplied.
/// </param>
/// <param name="SaveAsTenantDefault">
/// Whether the key should be written as a tenant-scoped secret
/// (<c>true</c>) or a unit-scoped override (<c>false</c>). Meaningful
/// only when <see cref="SecretName"/> is non-null.
/// </param>
/// <param name="ErrorMessage">
/// Non-null when the flag combination was rejected. Callers surface
/// this verbatim on stderr and exit 1.
/// </param>
public sealed record UnitCredentialOptions(
    string Key,
    string? SecretName,
    bool SaveAsTenantDefault,
    string? ErrorMessage)
{
    /// <summary>No credential flags supplied — no secret write planned.</summary>
    public static UnitCredentialOptions None() =>
        new(string.Empty, SecretName: null, SaveAsTenantDefault: false, ErrorMessage: null);

    /// <summary>Flag combination rejected; the caller must surface the message and exit.</summary>
    public static UnitCredentialOptions Rejected(string message) =>
        new(string.Empty, SecretName: null, SaveAsTenantDefault: false, ErrorMessage: message);
}