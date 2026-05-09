// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.ErrorHandling;
using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

using Microsoft.Kiota.Abstractions;

/// <summary>
/// Builds the "unit" command tree for unit management.
/// </summary>
public static class UnitCommand
{
    /// <summary>
    /// Stderr message used by the legacy <c>--container-runtime</c> flag's
    /// parser rejection on <c>spring unit create</c> (ADR-0039 §7 / §9).
    /// Pinned by tests so a future flag rename doesn't slip past CI.
    /// </summary>
    /// <remarks>
    /// ADR-0039 §7 makes the container runtime platform configuration: the
    /// host process picks one runtime at deploy time and every agent on
    /// that host uses it. The rejection is parser-level only — there is no
    /// stub command, no aliased handler. Operators see the migration path
    /// before any action runs.
    /// </remarks>
    public const string LegacyContainerRuntimeFlagRejectionMessage =
        "--container-runtime was removed in ADR-0039. The container runtime is platform " +
        "configuration: the host picks one runtime at deploy time and every agent on that " +
        "host uses it. Drop the flag — there is no per-unit override.";

    private static readonly OutputFormatter.Column<UnitResponse>[] UnitColumns =
    {
        new("id", u => GuidDisplay.Format(u.Id)),
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
    /// Unified member-list row emitted by <c>unit members list</c> (#352, #1028, #1060).
    /// Field names now mirror the API's <c>UnitMembershipResponse</c> wire shape
    /// (<c>unitId</c>, <c>agentAddress</c>, <c>member</c>, plus <c>createdAt</c> /
    /// <c>updatedAt</c> / <c>isPrimary</c>) so scripts consuming <c>GET /memberships</c>,
    /// the <c>members add</c> response, and <c>members list --output json</c> can
    /// share one jq expression. Agent-scheme rows carry per-membership config
    /// overrides; unit-scheme rows leave the agent-only fields null because
    /// sub-unit memberships have no per-child config today (deferred to #217) —
    /// their member identity is carried in <c>subUnitId</c> instead. The
    /// explicit <c>Scheme</c> column lets scripts filter with
    /// <c>jq '.[] | select(.scheme == "unit")'</c>; the unified <c>Member</c>
    /// column carries the scheme-prefixed canonical address
    /// (<c>agent://{path}</c> or <c>unit://{path}</c>) so scripts that just
    /// want "the address of this member" don't have to coalesce
    /// <c>agentAddress</c> with <c>subUnitId</c> per row.
    /// </summary>
    private sealed record MemberListRow(
        string Scheme,
        string UnitId,
        string? AgentAddress,
        string? SubUnitId,
        string? Member,
        string? Model,
        string? Specialty,
        bool? Enabled,
        string? ExecutionMode,
        DateTimeOffset? CreatedAt,
        DateTimeOffset? UpdatedAt,
        bool? IsPrimary);

    // Table columns preserve the pre-#1028 "scheme / member / unit" human-readable
    // layout so terminal output stays stable; the `member` table cell shows the
    // bare slug (agent or sub-unit) for readability while the JSON `member`
    // field carries the scheme-prefixed canonical address (#1060).
    private static readonly OutputFormatter.Column<MemberListRow>[] MemberListColumns =
    {
        new("scheme", r => r.Scheme),
        new("member", r => r.AgentAddress ?? r.SubUnitId),
        new("unit", r => r.UnitId),
        new("model", r => r.Model),
        new("specialty", r => r.Specialty),
        new("enabled", r => r.Enabled?.ToString().ToLowerInvariant()),
        new("executionMode", r => r.ExecutionMode),
    };

    // #1060: scheme-prefixed canonical address used by the JSON `member`
    // field. Mirrors the server-side projection in MembershipEndpoints —
    // kept inline (rather than reaching into Cvoya.Spring.Core.Messaging.Address)
    // because the CLI builds rows from the Kiota wire types here, and the
    // projection is the same tiny string concat the core helper does.
    private static string? BuildMemberUri(string? scheme, string? path)
        => string.IsNullOrEmpty(scheme) || string.IsNullOrEmpty(path)
            ? null
            : $"{scheme}://{path}";

    /// <summary>
    /// Creates the "unit" command with subcommands for CRUD, member operations,
    /// and the cascading purge helper.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var unitCommand = new Command("unit", "Manage units");

        unitCommand.Subcommands.Add(CreateListCommand(outputOption));
        unitCommand.Subcommands.Add(CreateCreateCommand(outputOption));
        // #1629 PR6 — `show <id-or-name>` accepts a Guid (canonical no-dash
        // 32-hex or dashed) for direct lookup, OR a display_name for search
        // with disambiguation. `--unit <parent-name-or-guid>` constrains the
        // search to children of that parent. Distinct from `status`, which
        // returns the lifecycle / readiness state.
        unitCommand.Subcommands.Add(CreateShowCommand(outputOption));
        // ADR-0035 decision 4: `create-from-template` is deleted outright;
        // `spring package install` is the replacement.
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

            var result = await client.ListUnitsAsync(ct: ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, UnitColumns));
        });

        return command;
    }

    private static Command CreateCreateCommand(Option<string> outputOption)
    {
        // "name" is the unit's address path and unique identifier; the server
        // generates the actor id.
        //
        // ADR-0039 §8 audit (task H2, issue #1870): `spring agent create`'s
        // positional `<id>` was removed in H1 because it was a legacy `Name`
        // string the server replaced with an allocated Guid. The unit
        // command's positional `name` is *not* analogous in semantics — it
        // is the unit's persistent address path consumed by every other
        // `spring unit *` verb (`unit start <name>`, `unit show <name>`,
        // `unit members list <name>`, …) and by the `/api/v1/units/{name}`
        // route shape. Removing it requires a coordinated redesign across
        // every unit verb and API route (Guid-only addressing per ADR-0036
        // / ADR-0039 §1). That redesign is out of H2's scope; the
        // positional is intentionally retained here.
        var nameArg = new Argument<string?>("name")
        {
            Description = "The unit name (address path; also used as the identifier).",
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
                "(claude-code / codex / gemini / spring-voyage); validation happens at unit activation.",
        };
        var colorOption = new Option<string?>("--color")
        {
            Description = "Optional UI accent colour hint (e.g. #6366f1).",
        };
        // ADR-0038: agent-runtime selection is `--runtime <id>`. The
        // structured model surface lives behind unit-create only as a
        // shorthand for the credential-write target — provider id is
        // captured by --model-provider when supplied. Unit-level
        // execution defaults (image / container-runtime / runtime /
        // model) belong on `spring unit execution set`, not here.
        var runtimeOption = new Option<string?>("--runtime")
        {
            Description = "Agent runtime id (e.g. claude-code, codex, gemini, spring-voyage). " +
                "Drives the credential write target for --api-key / --api-key-from-file via the runtime → provider edge.",
        };
        var modelProviderOption = new Option<string?>("--model-provider")
        {
            Description = "Model-provider id (e.g. anthropic, openai, google, ollama). " +
                "Required for multi-provider runtimes (spring-voyage); optional for fixed-provider runtimes.",
        };

        // ADR-0038 §7: legacy --agent flag is rejected at parse time.
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
        // ADR-0038: legacy flat --provider is rejected.
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
        // ADR-0039 §7: container runtime is platform configuration, not an
        // operator-facing unit-create choice. The flag is rejected at parse
        // time with the §9 migration hint so callers see the change before
        // any action runs.
        var legacyContainerRuntimeOption = new Option<string?>("--container-runtime")
        {
            Description = "REJECTED — container runtime is platform configuration in ADR-0039.",
            Hidden = true,
        };
        legacyContainerRuntimeOption.Validators.Add(result =>
        {
            if (result.Tokens.Count > 0)
            {
                result.AddError(LegacyContainerRuntimeFlagRejectionMessage);
            }
        });
        var hostingOption = new Option<string?>("--hosting")
        {
            Description = "Agent hosting mode (ephemeral, persistent).",
        };
        hostingOption.AcceptOnlyFromAmong("ephemeral", "persistent");

        // #626: inline credential entry. Pair these flags with --runtime
        // (and optionally --model-provider for multi-provider runtimes)
        // to supply the LLM API key at unit-create time. See
        // `UnitCredentialOptions` for the full rejection matrix.
        var apiKeyOption = new Option<string?>("--api-key")
        {
            Description =
                "LLM API key for the chosen provider (set inline). Rejected when the runtime / provider has no key (e.g. ollama). Mutually exclusive with --api-key-from-file.",
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
        command.Options.Add(runtimeOption);
        command.Options.Add(modelProviderOption);
        command.Options.Add(legacyAgentOption);
        command.Options.Add(legacyProviderOption);
        command.Options.Add(legacyContainerRuntimeOption);
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
            var runtimeId = parseResult.GetValue(runtimeOption);
            var hosting = parseResult.GetValue(hostingOption);
            var apiKey = parseResult.GetValue(apiKeyOption);
            var apiKeyFromFile = parseResult.GetValue(apiKeyFromFileOption);
            var saveAsTenantDefault = parseResult.GetValue(saveAsTenantDefaultOption);
            // Post-#1629 parent-unit ids are stable Guids; the CLI parses
            // both no-dash and dashed forms, surfaces a friendly error
            // before the API call when any value is malformed.
            var parentUnitsRaw = (parseResult.GetValue(parentUnitOption) ?? Array.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p.Trim())
                .ToArray();
            var parentUnits = new List<Guid>(parentUnitsRaw.Length);
            foreach (var raw in parentUnitsRaw)
            {
                if (!Guid.TryParse(raw, out var parentGuid))
                {
                    await Console.Error.WriteLineAsync(
                        $"Invalid parent-unit id '{raw}': expected a Guid.");
                    Environment.Exit(1);
                    return;
                }
                parentUnits.Add(parentGuid);
            }
            var topLevel = parseResult.GetValue(topLevelOption);
            var noWait = parseResult.GetValue(noWaitOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            // Review feedback on #744: reject neither / both at parse time
            // so callers see a local error instead of the server's 400.
            if (topLevel && parentUnits.Count > 0)
            {
                await Console.Error.WriteLineAsync(
                    "--top-level and --parent-unit are mutually exclusive. Supply exactly one.");
                Environment.Exit(1);
                return;
            }
            if (!topLevel && parentUnits.Count == 0)
            {
                await Console.Error.WriteLineAsync(
                    "Every unit must have a parent. Supply one or more --parent-unit <id> flags, "
                    + "or pass --top-level to attach the unit directly to the tenant.");
                Environment.Exit(1);
                return;
            }

            // ADR-0038: when an inline credential is supplied, the
            // operator MUST also name the runtime via --runtime so the
            // CLI knows which provider edge owns the secret. The
            // resolver consults the runtime catalogue to pick the
            // provider id and hands that to the install service.
            var credentialClient = ClientFactory.Create();
            var credentialResolution = await ResolveCredentialOptionsAsync(
                runtimeId,
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

            // ADR-0035 decision 4: `--from-template` is removed. To install a
            // package, use `spring package install`. To create a unit from scratch,
            // supply the unit name as the positional argument.
            if (string.IsNullOrWhiteSpace(positionalName))
            {
                await Console.Error.WriteLineAsync(
                    "Missing unit name. Supply it as the first argument. "
                    + "To install a package, use 'spring package install <package-name>'.");
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
                        $"Failed to write tenant default '{credentialResolution.SecretName}': {Utilities.ProblemDetailsFormatter.Format(ex)}");
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
                hosting: hosting,
                parentUnitIds: parentUnits.Count > 0 ? (IReadOnlyList<Guid>)parentUnits : null,
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
                        propagate: null,
                        ct: ct);
                }
                catch (Exception ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"warning: unit secret '{credentialResolution.SecretName}' not written: {Utilities.ProblemDetailsFormatter.Format(ex)}");
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
                await Console.Error.WriteLineAsync(
                    $"Failed to revalidate unit '{name}': {ExtractServerDetail(ex)}");
                // #990: route through ForCode so scripts can branch on the
                // specific validation failure (20-27) rather than the generic
                // UnknownError (1). ForCode returns UnknownError when the code
                // is absent or unknown, so the fallback is unchanged.
                Environment.Exit(ExtractValidationExitCode(ex));
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
    /// Kiota <see cref="ApiException"/>. When the server returned a
    /// structured RFC-7807 ProblemDetails body the Kiota runtime throws a
    /// <see cref="ProblemDetails"/>, whose default
    /// <see cref="Exception.Message"/> is the type-name string. Route
    /// through <see cref="Utilities.ProblemDetailsFormatter"/> so
    /// operators see the server's Title/Detail/extensions instead.
    /// </summary>
    internal static string ExtractServerDetail(ApiException ex)
    {
        var message = Utilities.ProblemDetailsFormatter.Format(ex);
        return string.IsNullOrWhiteSpace(message)
            ? "server rejected the request."
            : message;
    }

    /// <summary>
    /// #990: Extracts the <c>code</c> extension from a
    /// <see cref="ProblemDetails"/> response and returns the documented
    /// 20–27 exit code for the recognised validation failures, or
    /// <see cref="UnitValidationExitCodes.UnknownError"/> (1) for anything
    /// else. The message output is unchanged — this only affects the exit code.
    /// </summary>
    internal static int ExtractValidationExitCode(ApiException ex)
    {
        if (ex is ProblemDetails problem
            && problem.AdditionalData is { Count: > 0 } data
            && data.TryGetValue("code", out var raw))
        {
            var codeString = raw switch
            {
                string s => s,
                Microsoft.Kiota.Abstractions.Serialization.UntypedString us => us.GetValue(),
                _ => null,
            };

            if (!string.IsNullOrEmpty(codeString))
            {
                var mapped = UnitValidationExitCodes.ForCode(codeString);
                if (mapped != UnitValidationExitCodes.UnknownError)
                {
                    return mapped;
                }
            }
        }

        return UnitValidationExitCodes.UnknownError;
    }

    /// <summary>
    /// #1027: detects the API's 409 "agent's last unit membership" response
    /// (thrown by <c>MembershipEndpoints.DeleteMembershipAsync</c> when the
    /// repository surfaces <c>AgentMembershipRequiredException</c>). Matched
    /// on status + canonical title so the cascading purge in
    /// <c>CreatePurgeCommand</c> can fall through to <c>DeleteAgentAsync</c>
    /// to complete the #652 cascade contract without breaking the
    /// every-agent-has-&#x2265;1-unit invariant.
    /// </summary>
    private const string LastMembershipConflictTitle = "Agent must belong to at least one unit";

    private static bool IsLastMembershipConflict(ApiException ex)
    {
        if (ex.ResponseStatusCode != 409)
        {
            return false;
        }
        return ex is ProblemDetails problem
            && string.Equals(problem.Title, LastMembershipConflictTitle, StringComparison.Ordinal);
    }

    // #1629 PR6 — show columns. `show` is the read-on-name verb: it
    // resolves a Guid OR a display_name to a single unit and prints its
    // canonical identity. We surface a slimmer column set than `status`
    // because `status` is the lifecycle/readiness verb (status, ready,
    // missing) and `show` is the identity verb.
    private static readonly OutputFormatter.Column<UnitResponse>[] UnitShowColumns =
    {
        new("id", u => GuidDisplay.Format(u.Id)),
        new("displayName", u => u.DisplayName ?? u.Name),
        new("description", u => u.Description),
        new("hosting", u => u.Hosting),
        // ADR-0038: provider is intrinsic to ai.model.provider; flat slot is gone.
        new("model", u => u.Model),
    };

    private static Command CreateShowCommand(Option<string> outputOption)
    {
        // #1629 final design: every `show` accepts Guid OR display_name.
        // Guid input short-circuits to a direct lookup; name input goes
        // through CliResolver and emits a disambiguation list on n-match.
        var idArg = new Argument<string>("id-or-name")
        {
            Description =
                "The unit's stable Guid (32-char no-dash hex or dashed form), " +
                "OR a display_name to search for. When a name is supplied and " +
                "multiple units match, a disambiguation list is printed with " +
                "each candidate's Guid.",
        };
        var unitOption = new Option<string?>("--unit")
        {
            Description =
                "Optional parent-unit context (Guid or display_name) used to " +
                "constrain a name search to children of that parent. Ignored " +
                "when the first argument is itself a Guid.",
        };
        var command = new Command(
            "show",
            "Resolve a unit by Guid OR display_name (with optional --unit parent context) and print its identity. " +
            "Exits non-zero with a disambiguation list when the name search is ambiguous.");
        command.Arguments.Add(idArg);
        command.Options.Add(unitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(idArg)!;
            var unitArg = parseResult.GetValue(unitOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);

            // Resolve the optional parent-unit context first; same 0/1/n
            // contract — cleanest to surface "the parent isn't found"
            // before drilling into the children.
            Guid? parentContext = null;
            if (!string.IsNullOrWhiteSpace(unitArg))
            {
                try
                {
                    parentContext = await resolver.ResolveUnitAsync(unitArg, parentContext: null, ct);
                }
                catch (CliResolutionException ex)
                {
                    CliResolutionPrinter.Write(Console.Error, ex);
                    Environment.Exit(1);
                    return;
                }
            }

            Guid unitId;
            try
            {
                unitId = await resolver.ResolveUnitAsync(idOrName, parentContext, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            var canonical = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unitId);

            try
            {
                var detail = await client.GetUnitAsync(canonical, ct);
                var unit = detail.Unit;

                if (unit is null)
                {
                    await Console.Error.WriteLineAsync(
                        $"Unit '{canonical}' resolved but the server returned an empty payload.");
                    Environment.Exit(1);
                    return;
                }

                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(unit)
                    : OutputFormatter.FormatTable(unit, UnitShowColumns));
            }
            catch (ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                await Console.Error.WriteLineAsync($"No unit found matching '{idOrName}'.");
                Environment.Exit(1);
            }
        });

        return command;
    }


    private static Command CreateDeleteCommand()
    {
        var idArg = new Argument<string>("id") { Description = "The unit identifier" };
        var forceOption = new Option<bool>("--force")
        {
            Description =
                "Bypass the lifecycle-status gate and delete the unit even " +
                "if it is in Validating, Starting, Running, or Stopping. Use " +
                "to recover units the API otherwise refuses to delete with " +
                "409 (e.g. probes that crashed or scheduler-side failures " +
                "before #1136 landed). Best-effort — the unit is removed " +
                "from the directory regardless of in-flight teardown work.",
        };
        var command = new Command("delete", "Delete a unit");
        command.Arguments.Add(idArg);
        command.Options.Add(forceOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var force = parseResult.GetValue(forceOption);
            var client = ClientFactory.Create();

            await client.DeleteUnitAsync(id, force, ct);
            Console.WriteLine(force
                ? $"Unit '{id}' force-deleted."
                : $"Unit '{id}' deleted.");
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
        var forceOption = new Option<bool>("--force")
        {
            Description =
                "Forward --force to the final unit-delete step so the cascade " +
                "completes even when the root unit is in a stuck state " +
                "(Validating, Starting, Running, Stopping). Has no effect on " +
                "the per-membership deletes — only on the final " +
                "DeleteUnitAsync(id) call.",
        };
        var command = new Command(
            "purge",
            "Cascading cleanup: delete every membership row for the unit, then delete the unit itself. Requires --confirm because it is destructive.");
        command.Arguments.Add(idArg);
        command.Options.Add(confirmOption);
        command.Options.Add(forceOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var id = parseResult.GetValue(idArg)!;
            var confirm = parseResult.GetValue(confirmOption);
            var force = parseResult.GetValue(forceOption);
            if (!confirm)
            {
                await Console.Error.WriteLineAsync(
                    $"Refusing to purge unit '{id}' without --confirm. Re-run with --confirm to proceed.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            var renderContext = ErrorHandling.RenderContextFactory.For(
                parseResult, $"Failed to purge unit '{id}'");

            try
            {
                // Step 1: enumerate memberships so the user sees exactly what is cascading.
                var memberships = await client.ListUnitMembershipsAsync(id, ct);
                Console.WriteLine(
                    $"Purging unit '{id}': {memberships.Count} membership(s) to remove before the unit itself.");

                // Step 2: delete each membership row. When the API refuses the
                // delete with 409 "agent's last unit membership" (#744 / #823),
                // honour the #652 cascade contract by deleting the agent
                // itself — that path cascades through the repository's
                // DeleteAllForAgentAsync and removes the membership edge at
                // the same time. Any other ApiException falls through to the
                // outer catch so the operator sees the server's message
                // verbatim instead of a Kiota stack trace (#1026).
                foreach (var membership in memberships)
                {
                    var agentAddress = membership.AgentAddress ?? string.Empty;
                    Console.WriteLine($"  - removing membership for agent '{agentAddress}'");
                    try
                    {
                        await client.DeleteMembershipAsync(id, agentAddress, ct);
                    }
                    catch (ApiException ex) when (IsLastMembershipConflict(ex))
                    {
                        Console.WriteLine(
                            $"    - last unit membership for agent '{agentAddress}'; deleting the agent to complete the cascade");
                        await client.DeleteAgentAsync(agentAddress, ct);
                    }
                }

                // Step 3: delete the unit. --force is forwarded here so a
                // root unit stuck in a non-terminal state still gets
                // tombstoned (#1137).
                Console.WriteLine(force
                    ? $"  - force-deleting unit '{id}'"
                    : $"  - deleting unit '{id}'");
                await client.DeleteUnitAsync(id, force, ct);
                Console.WriteLine($"Unit '{id}' purged.");
            }
            catch (ApiException ex)
            {
                // #1068: route through the central renderer so the JSON
                // path mirrors the prose path — both surface the
                // forceHint/hint extensions the API emits on the
                // "stop before purging" gate so scripts can auto-recover.
                var exitCode = ErrorHandling.ApiExceptionRenderer.Instance.Render(ex, renderContext);
                Environment.Exit(exitCode);
                return;
            }
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
                await Console.Error.WriteLineAsync(
                    $"Failed to start unit '{name}': {ExtractServerDetail(ex)}");
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
                await Console.Error.WriteLineAsync(
                    $"Failed to stop unit '{name}': {ExtractServerDetail(ex)}");
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
                await Console.Error.WriteLineAsync(
                    $"Failed to get status for unit '{name}': {ExtractServerDetail(ex)}");
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
                        UnitId: m.UnitId ?? unitId,
                        AgentAddress: path,
                        SubUnitId: null,
                        // #1060: prefer the API-side `member` value when
                        // present so the CLI's JSON shape stays a strict
                        // superset of the HTTP wire shape. Falls back to the
                        // locally-built canonical URI for older servers.
                        Member: m.Member ?? BuildMemberUri("agent", path),
                        Model: m.Model,
                        Specialty: m.Specialty,
                        Enabled: m.Enabled,
                        ExecutionMode: m.ExecutionMode?.AgentExecutionMode?.ToString(),
                        CreatedAt: m.CreatedAt,
                        UpdatedAt: m.UpdatedAt,
                        IsPrimary: m.IsPrimary));
                    seenAgents.Add(path);
                }
                else
                {
                    var isAgent = string.Equals(scheme, "agent", StringComparison.Ordinal);
                    rows.Add(new MemberListRow(
                        Scheme: scheme,
                        UnitId: unitId,
                        AgentAddress: isAgent ? path : null,
                        SubUnitId: isAgent ? null : path,
                        Member: BuildMemberUri(scheme, path),
                        Model: null,
                        Specialty: null,
                        Enabled: null,
                        ExecutionMode: null,
                        CreatedAt: null,
                        UpdatedAt: null,
                        IsPrimary: null));
                    if (isAgent)
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
                    UnitId: m.UnitId ?? unitId,
                    AgentAddress: address,
                    SubUnitId: null,
                    Member: m.Member ?? BuildMemberUri("agent", address),
                    Model: m.Model,
                    Specialty: m.Specialty,
                    Enabled: m.Enabled,
                    ExecutionMode: m.ExecutionMode?.AgentExecutionMode?.ToString(),
                    CreatedAt: m.CreatedAt,
                    UpdatedAt: m.UpdatedAt,
                    IsPrimary: m.IsPrimary));
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
                        $"Failed to add unit '{childUnitId}' as a member of '{parentUnitId}': {ExtractServerDetail(ex)}");
                    Environment.Exit(1);
                    return;
                }

                Console.WriteLine($"Unit '{childUnitId}' added as a member of '{parentUnitId}'.");
                return;
            }

            // Agent path: run the public assignment endpoint first so
            // server-side membership-graph validation fires, then preserve
            // the existing override output shape through the upsert flow.
            await InvokeUpsertAsync(parseResult, unitArg, bind, outputOption, assignAgent: true, ct);
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
            await InvokeUpsertAsync(parseResult, unitArg, bind, outputOption, assignAgent: false, ct));

        return command;
    }

    private static Command CreateMembersRemoveCommand()
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        // #1151: --agent and --unit are mutually exclusive (exactly one
        // required). Mirrors the shape of `members add` (#331) so the
        // remove path can detach either an agent membership or a sub-unit
        // edge through a single verb. Both options are parser-permissive
        // (Required = false) so the action body can produce a single,
        // readable error when callers supply neither / both — same pattern
        // used by `members add`.
        var agentOption = new Option<string?>("--agent")
        {
            Description = "The agent identifier to remove from this unit (mutually exclusive with --unit).",
        };
        var unitOption = new Option<string?>("--unit")
        {
            Description = "The sub-unit identifier to detach from this unit (mutually exclusive with --agent).",
        };
        var command = new Command(
            "remove",
            "Remove an agent (--agent) or detach a sub-unit (--unit) from this unit. Exactly one of --agent or --unit must be supplied.");
        command.Arguments.Add(unitArg);
        command.Options.Add(agentOption);
        command.Options.Add(unitOption);

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

            var client = ClientFactory.Create();

            if (hasChildUnit)
            {
                // #1151: sub-unit memberships live on the unit actor's
                // member list rather than the /memberships repository
                // (per-membership overrides are agent-only today, see
                // #217). The actor-level DELETE /api/v1/units/{id}/members/
                // {memberId} endpoint handles both schemes server-side and
                // consults UnitParentInvariantGuard, so removing the last
                // parent of a non-top-level child returns 409 — bubbled
                // through ExtractServerDetail like every other structured
                // error path.
                try
                {
                    await client.RemoveMemberAsync(parentUnitId, childUnitId!, ct);
                }
                catch (ApiException ex)
                {
                    await Console.Error.WriteLineAsync(
                        $"Failed to detach sub-unit '{childUnitId}' from unit '{parentUnitId}': {ExtractServerDetail(ex)}");
                    Environment.Exit(1);
                    return;
                }

                Console.WriteLine($"Sub-unit '{childUnitId}' detached from unit '{parentUnitId}'.");
                return;
            }

            try
            {
                await client.DeleteMembershipAsync(parentUnitId, agentId!, ct);
            }
            catch (ApiException ex)
            {
                // #1026: route the 409 "last membership" ProblemDetails (and
                // every other structured error) through the shared formatter
                // so operators see the server's title/detail rather than the
                // Kiota exception stack.
                await Console.Error.WriteLineAsync(
                    $"Failed to remove membership for agent '{agentId}' from unit '{parentUnitId}': {ExtractServerDetail(ex)}");
                Environment.Exit(1);
                return;
            }
            Console.WriteLine($"Membership for agent '{agentId}' removed from unit '{parentUnitId}'.");
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
        bool assignAgent,
        CancellationToken ct)
    {
        var unitId = parseResult.GetValue(unitArg)!;
        var inputs = bind(parseResult);
        var output = parseResult.GetValue(outputOption) ?? "table";
        var client = ClientFactory.Create();

        UnitMembershipResponse result;
        try
        {
            if (assignAgent)
            {
                await client.AssignUnitAgentAsync(unitId, inputs.AgentId, ct);
            }

            result = await client.UpsertMembershipAsync(
                unitId,
                inputs.AgentId,
                inputs.Model,
                inputs.Specialty,
                inputs.Enabled,
                inputs.ExecutionMode,
                ct);
        }
        catch (ApiException ex)
        {
            if (await TryWriteMultiParentInheritanceConflictAsync(client, ex, Console.Error, ct))
            {
                Environment.Exit(1);
                return;
            }

            // #1026: surface the server's ProblemDetails (title / detail /
            // extensions) instead of letting the raw Kiota exception leak
            // as an unformatted stack trace. Exit 1 so scripts can detect
            // the failure without parsing stderr.
            await Console.Error.WriteLineAsync(
                $"Failed to upsert membership for agent '{inputs.AgentId}' in unit '{unitId}': {ExtractServerDetail(ex)}");
            Environment.Exit(1);
            return;
        }

        Console.WriteLine(output == "json"
            ? OutputFormatter.FormatJson(result)
            : OutputFormatter.FormatTable(result, MembershipColumns));
    }

    internal static async Task<bool> TryWriteMultiParentInheritanceConflictAsync(
        SpringApiClient client,
        ApiException exception,
        System.IO.TextWriter writer,
        CancellationToken ct)
    {
        if (!MultiParentInheritanceConflictFormatter.TryParse(exception, out var conflict))
        {
            return false;
        }

        var labels = await ResolveConflictUnitLabelsAsync(client, conflict, ct);
        foreach (var line in MultiParentInheritanceConflictFormatter.FormatLines(conflict, labels))
        {
            await writer.WriteLineAsync(line);
        }

        return true;
    }

    private static async Task<IReadOnlyDictionary<string, string>> ResolveConflictUnitLabelsAsync(
        SpringApiClient client,
        MultiParentInheritanceConflict conflict,
        CancellationToken ct)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var unitId in conflict.UnitIds)
        {
            labels[unitId] = unitId;

            if (!Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(unitId, out _))
            {
                continue;
            }

            try
            {
                var detail = await client.GetUnitAsync(unitId, ct);
                var label = detail.Unit?.DisplayName ?? detail.Unit?.Name;
                if (!string.IsNullOrWhiteSpace(label))
                {
                    labels[unitId] = label!;
                }
            }
            catch
            {
                // The conflict itself is authoritative. If the best-effort
                // display-name lookup fails, keep the server-supplied id so
                // the operator still sees every conflicting parent.
            }
        }

        return labels;
    }

    private sealed record MembershipInputs(
        string AgentId,
        string? Model,
        string? Specialty,
        bool? Enabled,
        AgentExecutionMode? ExecutionMode);

    // #1732: ValidateProviderModelAgainstTool / DeriveRequiredRuntimeId
    // were the pre-#1732 tool-to-runtime bridge. Now that the runtime id
    // is the input directly (--agent on `unit create` / `agent create`),
    // both helpers are obsolete and were removed.

    /// <summary>
    /// ADR-0038: adapter over <see cref="SpringApiClient.GetModelProviderAsync"/>
    /// that satisfies the
    /// <c>Func&lt;string, CancellationToken, Task&lt;string?&gt;&gt;</c>
    /// resolver signature expected by
    /// <see cref="ResolveCredentialOptionsAsync"/>. Returns the provider's
    /// <c>CredentialSecretName</c> verbatim — <c>null</c> when the provider
    /// is not installed on the current tenant, <see cref="string.Empty"/>
    /// when the provider declares no credential (for example Ollama).
    /// </summary>
    /// <remarks>
    /// PR-2 will rewire the unit / agent CLI commands to ask for a
    /// provider id directly; in Chunk A this resolver is invoked with the
    /// runtime id which the host translates to a provider id internally.
    /// </remarks>
    private static Func<string, CancellationToken, Task<string?>> RuntimeSecretNameResolver(
        SpringApiClient client)
        => async (providerId, ct) =>
        {
            var provider = await client.GetModelProviderAsync(providerId, ct);
            return provider?.CredentialSecretName;
        };

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
    /// The secret-name mapping is sourced from the runtime catalogue
    /// (ADR-0038) — the resolver passes the runtime id (or, in
    /// multi-provider runtimes, the provider id) to the platform's
    /// <c>GET /api/v1/tenant/model-providers/installs/{id}</c> which
    /// returns the canonical <c>credentialSecretName</c>. CLI, portal,
    /// and resolver stay in lock-step off the single authority.
    /// </remarks>
    public static async Task<UnitCredentialOptions> ResolveCredentialOptionsAsync(
        string? runtimeId,
        string? apiKey,
        string? apiKeyFromFile,
        bool saveAsTenantDefault,
        Func<string, CancellationToken, Task<string?>> credentialSecretNameResolver,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(credentialSecretNameResolver);

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

        // ADR-0038: --runtime names the credential routing key. The
        // platform translates the runtime id → provider id internally
        // when the install service writes the secret.
        var resolvedRuntimeId = string.IsNullOrWhiteSpace(runtimeId)
            ? null
            : runtimeId.Trim();
        if (resolvedRuntimeId is null)
        {
            return UnitCredentialOptions.Rejected(
                "--api-key / --api-key-from-file requires --runtime to name the runtime that owns the credential.");
        }

        // Ollama's runtime id is known but its credential secret name
        // is empty — "no credential to write", so the inline-key flags
        // have nowhere to land.
        var secretName = await credentialSecretNameResolver(resolvedRuntimeId, ct);
        if (secretName is null)
        {
            return UnitCredentialOptions.Rejected(
                $"Provider for runtime '{resolvedRuntimeId}' is not installed on the current tenant. " +
                "Install it (`spring model-provider install " + resolvedRuntimeId + "`) before supplying an API key.");
        }
        if (secretName.Length == 0)
        {
            return UnitCredentialOptions.Rejected(
                $"Runtime '{resolvedRuntimeId}' declares no credential (runs without an API key). " +
                "Drop --api-key / --api-key-from-file for this runtime.");
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
