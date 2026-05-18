// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;
using Cvoya.Spring.Cli.Utilities;

/// <summary>
/// Builds the <c>spring unit members humans</c> subtree (#2409). Targets the
/// REST surface introduced in the same issue —
/// <c>POST / GET / PATCH / DELETE /api/v1/tenant/units/{id}/members/humans</c>
/// — which writes to the <c>unit_memberships_humans</c> table (ADR-0044
/// § 3). Authorisation is enforced server-side: writes require
/// <see cref="Cvoya.Spring.Dapr.Actors.PermissionLevel.Owner"/> on the unit,
/// reads require <see cref="Cvoya.Spring.Dapr.Actors.PermissionLevel.Viewer"/>;
/// the CLI surfaces the server's failure verbatim instead of re-checking
/// locally.
/// </summary>
/// <remarks>
/// <para>
/// The verb tree mirrors <see cref="UnitHumansCommand"/> structurally but
/// operates on the orthogonal team-role membership table — not the unit
/// ACL grants surface. ADR-0044 § 1 splits the two facts: this verb
/// captures "who is on the team and in what role", while
/// <c>spring unit humans</c> captures "who can edit this unit".
/// </para>
/// <para>
/// <c>--human self</c> (or omitting <c>--human</c> entirely) resolves to
/// the authenticated caller's Human row via the existing
/// <c>GET /api/v1/tenant/auth/me</c> endpoint. <c>--roles</c>,
/// <c>--expertise</c>, and <c>--notifications</c> accept comma-separated
/// lists (or may be repeated) per ADR-0046 §3's multi-valued team-role
/// grammar and ADR-0044's "free-form, no vocabulary in v0.1" rule. The
/// natural key on the membership row is <c>(unit, human)</c>; a single
/// row carries the full multi-valued role list — there is no longer one
/// row per role.
/// </para>
/// </remarks>
public static class UnitMembersCommand
{
    private static readonly OutputFormatter.Column<UnitHumanMemberResponse>[] MemberColumns =
    {
        new("humanId", e => e.HumanId is { } id ? id.ToString("N") : null),
        new("roles", e => e.Roles is { Count: > 0 } r ? string.Join(",", r) : null),
        new("expertise", e => e.Expertise is { Count: > 0 } x ? string.Join(",", x) : null),
        new("notifications", e => e.Notifications is { Count: > 0 } n ? string.Join(",", n) : null),
        new("membershipId", e => e.MembershipId is { } mid ? mid.ToString("N") : null),
    };

    /// <summary>
    /// Entry point. Returns the <c>humans</c> subcommand tree for attachment
    /// under <c>unit members</c> — the existing <c>members</c> command
    /// already covers the unit-membership graph (agents + sub-units), so
    /// the new team-role surface plugs in as a parallel verb.
    /// </summary>
    public static Command CreateHumansSubcommand(Option<string> outputOption)
    {
        var humans = new Command(
            "humans",
            "Add / list / update / remove humans as team-role members of a unit (ADR-0044, separate from the unit's ACL grants).");
        humans.Subcommands.Add(CreateAddCommand(outputOption));
        humans.Subcommands.Add(CreateListCommand(outputOption));
        humans.Subcommands.Add(CreateUpdateCommand(outputOption));
        humans.Subcommands.Add(CreateRemoveCommand());
        return humans;
    }

    private static Command CreateAddCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier." };
        var humanOption = new Option<string?>("--human")
        {
            Description = "Stable human UUID. Pass 'self' or omit to resolve to the authenticated caller.",
        };
        var rolesOption = new Option<string[]?>("--roles")
        {
            Description =
                "Team-role list (free-form per ADR-0046 §3; e.g. owner or reviewer,security_lead). " +
                "Repeatable: pass --roles foo --roles bar, or use comma-separated form --roles foo,bar.",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var expertiseOption = new Option<string?>("--expertise")
        {
            Description = "Comma-separated expertise tags (free-form; empty list when omitted).",
        };
        var notificationsOption = new Option<string?>("--notifications")
        {
            Description = "Comma-separated notification event tags (free-form; empty list when omitted).",
        };

        var command = new Command(
            "add",
            "Add a human as a team-role member of this unit. Idempotent on (unit, human) " +
            "per ADR-0046 §7 — re-running replaces roles / expertise / notifications in place.");
        command.Arguments.Add(unitArg);
        command.Options.Add(humanOption);
        command.Options.Add(rolesOption);
        command.Options.Add(expertiseOption);
        command.Options.Add(notificationsOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitArgVal = parseResult.GetValue(unitArg)!;
            var humanArg = parseResult.GetValue(humanOption);
            var rolesRaw = parseResult.GetValue(rolesOption);
            var expertiseRaw = parseResult.GetValue(expertiseOption);
            var notificationsRaw = parseResult.GetValue(notificationsOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            var roles = FlattenMultiValued(rolesRaw);
            if (roles is null || roles.Count == 0)
            {
                await Console.Error.WriteLineAsync(
                    "--roles is required: pass at least one role (e.g. --roles owner or --roles reviewer,security_lead).");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);

            string unitId;
            try
            {
                unitId = await resolver.ResolveUnitIdAsync(unitArgVal, parentContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            Guid humanGuid;
            try
            {
                humanGuid = await ResolveHumanIdAsync(client, humanArg, ct);
            }
            catch (InvalidOperationException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message);
                Environment.Exit(1);
                return;
            }

            try
            {
                var response = await client.AddUnitHumanMemberAsync(
                    unitId,
                    humanGuid,
                    roles,
                    ParseTagList(expertiseRaw),
                    ParseTagList(notificationsRaw),
                    ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(response));
                }
                else
                {
                    var rolesDisplay = response.Roles is { Count: > 0 } r ? string.Join(", ", r) : "(none)";
                    Console.WriteLine(
                        $"Human '{humanGuid:N}' added to unit '{unitArgVal}' with roles [{rolesDisplay}].");
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to add team member '{humanGuid:N}' to unit '{unitArgVal}': {ProblemDetailsTranslator.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateListCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier." };

        var command = new Command(
            "list",
            "List every team-role membership row on this unit. Mirrors `sv.list_members`'s human entries.");
        command.Arguments.Add(unitArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitArgVal = parseResult.GetValue(unitArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";

            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);

            string unitId;
            try
            {
                unitId = await resolver.ResolveUnitIdAsync(unitArgVal, parentContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            try
            {
                var rows = await client.ListUnitHumanMembersAsync(unitId, ct);
                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(rows)
                    : OutputFormatter.FormatTable(rows, MemberColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to list team members for unit '{unitArgVal}': {ProblemDetailsTranslator.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateUpdateCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier." };
        var humanOption = new Option<string?>("--human")
        {
            Description = "Stable human UUID. Pass 'self' or omit to resolve to the authenticated caller.",
        };
        var rolesOption = new Option<string[]?>("--roles")
        {
            Description =
                "Replacement team-role list (free-form per ADR-0046 §3). Repeatable or comma-separated. " +
                "Omit to leave the existing roles unchanged; pass --roles \"\" to clear.",
            AllowMultipleArgumentsPerToken = true,
        };
        var expertiseOption = new Option<string?>("--expertise")
        {
            Description = "Replacement expertise tags (comma-separated; pass '' to clear).",
        };
        var notificationsOption = new Option<string?>("--notifications")
        {
            Description = "Replacement notification event tags (comma-separated; pass '' to clear).",
        };

        var command = new Command(
            "update",
            "Update the multi-valued roles / expertise / notifications on an existing team-role " +
            "membership row (keyed by (unit, human) per ADR-0046 §7). Each list-flag uses full-" +
            "replacement semantics: omit to leave unchanged, pass an empty value to clear.");
        command.Arguments.Add(unitArg);
        command.Options.Add(humanOption);
        command.Options.Add(rolesOption);
        command.Options.Add(expertiseOption);
        command.Options.Add(notificationsOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitArgVal = parseResult.GetValue(unitArg)!;
            var humanArg = parseResult.GetValue(humanOption);
            var rolesRaw = parseResult.GetValue(rolesOption);
            var expertiseRaw = parseResult.GetValue(expertiseOption);
            var notificationsRaw = parseResult.GetValue(notificationsOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            // Tri-state semantics: null when --roles was not passed at all
            // (leave unchanged); otherwise treat the supplied tokens as the
            // full replacement list. An explicit empty argument (e.g.
            // --roles "") collapses to a list with one empty token which
            // ParseTagList already strips down to Array.Empty<string>().
            var rolesSupplied = parseResult.GetResult(rolesOption) is not null;
            var roles = rolesSupplied ? FlattenMultiValued(rolesRaw) ?? Array.Empty<string>() : null;

            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);

            string unitId;
            try
            {
                unitId = await resolver.ResolveUnitIdAsync(unitArgVal, parentContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            Guid humanGuid;
            try
            {
                humanGuid = await ResolveHumanIdAsync(client, humanArg, ct);
            }
            catch (InvalidOperationException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message);
                Environment.Exit(1);
                return;
            }

            try
            {
                var response = await client.UpdateUnitHumanMemberAsync(
                    unitId,
                    humanGuid,
                    roles,
                    ParseTagList(expertiseRaw),
                    ParseTagList(notificationsRaw),
                    ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(response));
                }
                else
                {
                    var rolesDisplay = response.Roles is { Count: > 0 } r ? string.Join(", ", r) : "(none)";
                    Console.WriteLine(
                        $"Updated team-role membership for human '{humanGuid:N}' on unit '{unitArgVal}' (roles=[{rolesDisplay}]).");
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to update team member '{humanGuid:N}' on unit '{unitArgVal}': {ProblemDetailsTranslator.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateRemoveCommand()
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier." };
        var humanOption = new Option<string?>("--human")
        {
            Description = "Stable human UUID. Pass 'self' or omit to resolve to the authenticated caller.",
        };

        var command = new Command(
            "remove",
            "Remove a team-role membership row keyed by (unit, human) per ADR-0046 §7. " +
            "Idempotent — succeeds whether or not the row existed.");
        command.Arguments.Add(unitArg);
        command.Options.Add(humanOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitArgVal = parseResult.GetValue(unitArg)!;
            var humanArg = parseResult.GetValue(humanOption);

            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);

            string unitId;
            try
            {
                unitId = await resolver.ResolveUnitIdAsync(unitArgVal, parentContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            Guid humanGuid;
            try
            {
                humanGuid = await ResolveHumanIdAsync(client, humanArg, ct);
            }
            catch (InvalidOperationException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message);
                Environment.Exit(1);
                return;
            }

            try
            {
                await client.RemoveUnitHumanMemberAsync(unitId, humanGuid, ct);
                Console.WriteLine(
                    $"Removed team-role membership for human '{humanGuid:N}' on unit '{unitArgVal}'.");
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to remove team member '{humanGuid:N}' from unit '{unitArgVal}': {ProblemDetailsTranslator.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    /// <summary>
    /// Resolves the <c>--human</c> option. Accepts:
    /// <list type="bullet">
    ///   <item><description>Any standard Guid form (dashed or no-dash hex).</description></item>
    ///   <item><description>The literal string <c>self</c>.</description></item>
    ///   <item><description><see langword="null"/> / empty — resolves to the authenticated caller.</description></item>
    /// </list>
    /// </summary>
    private static async Task<Guid> ResolveHumanIdAsync(
        SpringApiClient client,
        string? humanArg,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(humanArg) &&
            !string.Equals(humanArg, "self", StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(humanArg, out var parsed))
            {
                throw new InvalidOperationException(
                    $"--human '{humanArg}' is not a valid Guid. Use the no-dash hex (32 chars), dashed Guid form, or 'self'.");
            }
            return parsed;
        }

        var me = await client.GetCurrentUserAsync(ct);
        if (me.Id is not { } id || id == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Server returned no caller UUID; pass --human <id> explicitly or run `spring auth login` first.");
        }
        return id;
    }

    /// <summary>
    /// Parses a comma-separated tag list. Returns <see langword="null"/>
    /// when the flag was not passed at all (no change), an empty list when
    /// the flag was passed with an empty value (clear semantics), or the
    /// trimmed tokens otherwise.
    /// </summary>
    public static IReadOnlyList<string>? ParseTagList(string? raw)
    {
        if (raw is null)
        {
            return null;
        }
        if (raw.Length == 0)
        {
            return Array.Empty<string>();
        }
        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    /// <summary>
    /// Flattens a multi-valued option's token list into a single trimmed
    /// list of tags. Each token is itself split on commas so the user can
    /// freely mix the repeated-flag form (<c>--roles a --roles b</c>) with
    /// the comma-separated form (<c>--roles a,b</c>). Returns
    /// <see langword="null"/> when no tokens were supplied at all.
    /// </summary>
    public static IReadOnlyList<string>? FlattenMultiValued(string[]? raw)
    {
        if (raw is null)
        {
            return null;
        }
        var tokens = new List<string>();
        foreach (var entry in raw)
        {
            if (string.IsNullOrEmpty(entry))
            {
                continue;
            }
            tokens.AddRange(entry.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return tokens;
    }

    /// <summary>
    /// Issue #2463: <c>spring unit members agents</c> verb tree. Single
    /// child verb <c>set</c> — the parallel to <c>members humans update</c>
    /// for editing the per-membership <c>roles</c> + <c>expertise</c>
    /// metadata on an agent ↔ unit membership row introduced by
    /// ADR-0046 §8. Does not touch <c>model</c>, <c>specialty</c>, or the
    /// other override columns (those flow through <c>unit members config</c>
    /// and the existing PUT membership endpoint).
    /// </summary>
    public static Command CreateAgentsSubcommand(Option<string> outputOption)
    {
        var agents = new Command(
            "agents",
            "Edit roles + expertise on agent members of a unit (ADR-0046 §8 / #2463).");
        agents.Subcommands.Add(CreateAgentMemberSetCommand(outputOption));
        return agents;
    }

    /// <summary>
    /// Issue #2463: <c>spring unit members units</c> verb tree. Edit
    /// surface for sub-unit-member <c>roles</c> + <c>expertise</c>
    /// projections — symmetric to <c>members agents set</c>. The
    /// <c>set</c> verb PATCHes the sub-unit ↔ parent-unit edge row;
    /// adding / removing sub-units themselves flows through the
    /// existing <c>spring unit members add</c> / <c>remove</c> verbs.
    /// </summary>
    public static Command CreateSubUnitsSubcommand(Option<string> outputOption)
    {
        var units = new Command(
            "units",
            "Edit roles + expertise on sub-unit members of a unit (ADR-0046 §8 extended to sub-units / #2463).");
        units.Subcommands.Add(CreateSubUnitMemberSetCommand(outputOption));
        return units;
    }

    private static Command CreateAgentMemberSetCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The parent unit identifier." };
        var agentOption = new Option<string>("--agent")
        {
            Description = "Stable agent UUID (32-char no-dash hex or dashed Guid form).",
            Required = true,
        };
        var rolesOption = new Option<string[]?>("--roles")
        {
            Description =
                "Replacement team-role list (free-form per ADR-0046 §8). Repeatable or comma-separated. " +
                "Omit to leave the existing roles unchanged; pass --roles \"\" to clear.",
            AllowMultipleArgumentsPerToken = true,
        };
        var expertiseOption = new Option<string?>("--expertise")
        {
            Description =
                "Replacement expertise tags (comma-separated). Omit to leave unchanged; pass '' to clear.",
        };

        var command = new Command(
            "set",
            "Set the per-membership roles + expertise on an agent member row (keyed by (unit, agent)). " +
            "Each list-flag uses full-replacement semantics: omit to leave unchanged, pass an empty value to clear.");
        command.Arguments.Add(unitArg);
        command.Options.Add(agentOption);
        command.Options.Add(rolesOption);
        command.Options.Add(expertiseOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitArgVal = parseResult.GetValue(unitArg)!;
            var agentArg = parseResult.GetValue(agentOption)!;
            var rolesRaw = parseResult.GetValue(rolesOption);
            var expertiseRaw = parseResult.GetValue(expertiseOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!Guid.TryParse(agentArg, out var agentGuid))
            {
                await Console.Error.WriteLineAsync(
                    $"--agent '{agentArg}' is not a valid Guid. Use the no-dash hex (32 chars) or dashed Guid form.");
                Environment.Exit(1);
                return;
            }

            // Tri-state: null when --roles was not passed at all (leave
            // unchanged); otherwise the supplied tokens become the full
            // replacement list. An explicit empty argument collapses to
            // Array.Empty<string>() via FlattenMultiValued.
            var rolesSupplied = parseResult.GetResult(rolesOption) is not null;
            var roles = rolesSupplied ? FlattenMultiValued(rolesRaw) ?? Array.Empty<string>() : null;
            var expertise = ParseTagList(expertiseRaw);

            if (roles is null && expertise is null)
            {
                await Console.Error.WriteLineAsync(
                    "At least one of --roles or --expertise must be supplied (omit both = no-op).");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);

            string unitId;
            try
            {
                unitId = await resolver.ResolveUnitIdAsync(unitArgVal, parentContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            try
            {
                var response = await client.UpdateUnitAgentMemberAsync(
                    unitId,
                    agentGuid,
                    roles,
                    expertise,
                    ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(response));
                }
                else
                {
                    var rolesDisplay = response.Roles is { Count: > 0 } r ? string.Join(", ", r) : "(none)";
                    var expertiseDisplay = response.Expertise is { Count: > 0 } x ? string.Join(", ", x) : "(none)";
                    Console.WriteLine(
                        $"Updated agent-member row for agent '{agentGuid:N}' on unit '{unitArgVal}' " +
                        $"(roles=[{rolesDisplay}], expertise=[{expertiseDisplay}]).");
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to update agent-member roles/expertise for agent '{agentGuid:N}' on unit '{unitArgVal}': {ProblemDetailsTranslator.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateSubUnitMemberSetCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The parent unit identifier." };
        var subUnitOption = new Option<string>("--sub-unit")
        {
            Description = "Stable sub-unit UUID (32-char no-dash hex or dashed Guid form).",
            Required = true,
        };
        var rolesOption = new Option<string[]?>("--roles")
        {
            Description =
                "Replacement team-role list (free-form per ADR-0046 §8). Repeatable or comma-separated. " +
                "Omit to leave the existing roles unchanged; pass --roles \"\" to clear.",
            AllowMultipleArgumentsPerToken = true,
        };
        var expertiseOption = new Option<string?>("--expertise")
        {
            Description =
                "Replacement expertise tags (comma-separated). Omit to leave unchanged; pass '' to clear.",
        };

        var command = new Command(
            "set",
            "Set the per-membership roles + expertise on a sub-unit member row (keyed by (parent unit, sub-unit)). " +
            "Each list-flag uses full-replacement semantics: omit to leave unchanged, pass an empty value to clear.");
        command.Arguments.Add(unitArg);
        command.Options.Add(subUnitOption);
        command.Options.Add(rolesOption);
        command.Options.Add(expertiseOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitArgVal = parseResult.GetValue(unitArg)!;
            var subUnitArg = parseResult.GetValue(subUnitOption)!;
            var rolesRaw = parseResult.GetValue(rolesOption);
            var expertiseRaw = parseResult.GetValue(expertiseOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!Guid.TryParse(subUnitArg, out var subUnitGuid))
            {
                await Console.Error.WriteLineAsync(
                    $"--sub-unit '{subUnitArg}' is not a valid Guid. Use the no-dash hex (32 chars) or dashed Guid form.");
                Environment.Exit(1);
                return;
            }

            var rolesSupplied = parseResult.GetResult(rolesOption) is not null;
            var roles = rolesSupplied ? FlattenMultiValued(rolesRaw) ?? Array.Empty<string>() : null;
            var expertise = ParseTagList(expertiseRaw);

            if (roles is null && expertise is null)
            {
                await Console.Error.WriteLineAsync(
                    "At least one of --roles or --expertise must be supplied (omit both = no-op).");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);

            string unitId;
            try
            {
                unitId = await resolver.ResolveUnitIdAsync(unitArgVal, parentContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            try
            {
                var response = await client.UpdateUnitSubUnitMemberAsync(
                    unitId,
                    subUnitGuid,
                    roles,
                    expertise,
                    ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(response));
                }
                else
                {
                    var rolesDisplay = response.Roles is { Count: > 0 } r ? string.Join(", ", r) : "(none)";
                    var expertiseDisplay = response.Expertise is { Count: > 0 } x ? string.Join(", ", x) : "(none)";
                    Console.WriteLine(
                        $"Updated sub-unit-member row for sub-unit '{subUnitGuid:N}' on parent unit '{unitArgVal}' " +
                        $"(roles=[{rolesDisplay}], expertise=[{expertiseDisplay}]).");
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to update sub-unit-member roles/expertise for sub-unit '{subUnitGuid:N}' on parent unit '{unitArgVal}': {ProblemDetailsTranslator.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }
}
