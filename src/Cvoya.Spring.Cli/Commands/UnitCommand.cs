// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

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
        unitCommand.Subcommands.Add(CreateDeleteCommand());
        unitCommand.Subcommands.Add(CreatePurgeCommand());
        unitCommand.Subcommands.Add(CreateMembersCommand(outputOption));

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
            Description = "Optional LLM model identifier (e.g. claude-sonnet-4-20250514).",
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
        var command = new Command("create", "Create a new unit");
        command.Arguments.Add(nameArg);
        command.Options.Add(displayNameOption);
        command.Options.Add(descriptionOption);
        command.Options.Add(modelOption);
        command.Options.Add(colorOption);
        command.Options.Add(fromTemplateOption);
        command.Options.Add(unitNameOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var positionalName = parseResult.GetValue(nameArg);
            var displayName = parseResult.GetValue(displayNameOption);
            var description = parseResult.GetValue(descriptionOption);
            var model = parseResult.GetValue(modelOption);
            var color = parseResult.GetValue(colorOption);
            var fromTemplate = parseResult.GetValue(fromTemplateOption);
            var unitNameOverride = parseResult.GetValue(unitNameOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!string.IsNullOrWhiteSpace(fromTemplate))
            {
                // --from-template path: positional 'name' is reinterpreted as
                // the override when --name is absent. This keeps the shell
                // ergonomics close to the direct-create form while the flag
                // spelling stays explicit for scripts.
                var effectiveUnitName = !string.IsNullOrWhiteSpace(unitNameOverride)
                    ? unitNameOverride
                    : positionalName;

                var slash = fromTemplate!.IndexOf('/');
                if (slash <= 0 || slash == fromTemplate.Length - 1)
                {
                    await Console.Error.WriteLineAsync(
                        "--from-template must be in the form <package>/<template-name>.");
                    Environment.Exit(1);
                    return;
                }

                var package = fromTemplate[..slash];
                var templateName = fromTemplate[(slash + 1)..];

                var client = ClientFactory.Create();
                var response = await client.CreateUnitFromTemplateAsync(
                    package,
                    templateName,
                    unitName: effectiveUnitName,
                    displayName: displayName,
                    model: model,
                    color: color,
                    ct: ct);

                // Surface server-side warnings (unresolved bundle tools,
                // binding previews) on both table and JSON output paths so
                // callers never miss the advisory messages.
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
                    // `response.Unit` is declared nullable by Kiota codegen;
                    // the server always populates it on a successful 201 so
                    // we surface a clear error rather than a blank table if
                    // that invariant ever broke.
                    var unit = response.Unit
                        ?? throw new InvalidOperationException(
                            "Server returned a from-template response with no unit envelope.");
                    Console.WriteLine(OutputFormatter.FormatTable(unit, UnitColumns));
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

            var directClient = ClientFactory.Create();
            var result = await directClient.CreateUnitAsync(
                positionalName!,
                displayName,
                description,
                model: model,
                color: color,
                ct: ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, UnitColumns));
        });

        return command;
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
            Description = "The sub-unit identifier to add as a member (mutually exclusive with --agent). See #331.",
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
}