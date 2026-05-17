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
/// <c>GET /api/v1/tenant/auth/me</c> endpoint. <c>--expertise</c> and
/// <c>--notifications</c> accept comma-separated lists per ADR-0044's
/// "free-form, no vocabulary in v0.1" rule.
/// </para>
/// </remarks>
public static class UnitMembersCommand
{
    private static readonly OutputFormatter.Column<UnitHumanMemberResponse>[] MemberColumns =
    {
        new("humanId", e => e.HumanId is { } id ? id.ToString("N") : null),
        new("role", e => e.Role),
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
        var roleOption = new Option<string>("--role")
        {
            Description = "Team role string (free-form per ADR-0044; e.g. owner, reviewer, security_lead).",
            Required = true,
        };
        var expertiseOption = new Option<string?>("--expertise")
        {
            Description = "Comma-separated expertise tags (free-form; empty list when omitted).",
        };
        var notificationsOption = new Option<string?>("--notification")
        {
            Description = "Comma-separated notification event tags (free-form; empty list when omitted).",
        };

        var command = new Command(
            "add",
            "Add a human as a team-role member of this unit. Idempotent on (unit, human, role) " +
            "— re-running with the same tuple updates expertise + notifications in place.");
        command.Arguments.Add(unitArg);
        command.Options.Add(humanOption);
        command.Options.Add(roleOption);
        command.Options.Add(expertiseOption);
        command.Options.Add(notificationsOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitArgVal = parseResult.GetValue(unitArg)!;
            var humanArg = parseResult.GetValue(humanOption);
            var role = parseResult.GetValue(roleOption)!;
            var expertiseRaw = parseResult.GetValue(expertiseOption);
            var notificationsRaw = parseResult.GetValue(notificationsOption);
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
                    role,
                    ParseTagList(expertiseRaw),
                    ParseTagList(notificationsRaw),
                    ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(response));
                }
                else
                {
                    Console.WriteLine(
                        $"Human '{humanGuid:N}' added to unit '{unitArgVal}' as '{response.Role}'.");
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
        var roleOption = new Option<string>("--role")
        {
            Description = "Team role string on the existing membership row.",
            Required = true,
        };
        var expertiseOption = new Option<string?>("--expertise")
        {
            Description = "Replacement expertise tags (comma-separated; pass '' to clear).",
        };
        var notificationsOption = new Option<string?>("--notification")
        {
            Description = "Replacement notification event tags (comma-separated; pass '' to clear).",
        };

        var command = new Command(
            "update",
            "Update expertise / notifications on an existing team-role membership row. The PATCH " +
            "replaces the whole tag set so omitted flags are treated as 'clear to empty'.");
        command.Arguments.Add(unitArg);
        command.Options.Add(humanOption);
        command.Options.Add(roleOption);
        command.Options.Add(expertiseOption);
        command.Options.Add(notificationsOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitArgVal = parseResult.GetValue(unitArg)!;
            var humanArg = parseResult.GetValue(humanOption);
            var role = parseResult.GetValue(roleOption)!;
            var expertiseRaw = parseResult.GetValue(expertiseOption);
            var notificationsRaw = parseResult.GetValue(notificationsOption);
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
                    role,
                    ParseTagList(expertiseRaw),
                    ParseTagList(notificationsRaw),
                    ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(response));
                }
                else
                {
                    Console.WriteLine(
                        $"Updated team-role membership for human '{humanGuid:N}' / role '{response.Role}' on unit '{unitArgVal}'.");
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
        var roleOption = new Option<string>("--role")
        {
            Description = "Team role string on the existing membership row.",
            Required = true,
        };

        var command = new Command(
            "remove",
            "Remove a team-role membership row. Idempotent — succeeds whether or not the row existed.");
        command.Arguments.Add(unitArg);
        command.Options.Add(humanOption);
        command.Options.Add(roleOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitArgVal = parseResult.GetValue(unitArg)!;
            var humanArg = parseResult.GetValue(humanOption);
            var role = parseResult.GetValue(roleOption)!;

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
                await client.RemoveUnitHumanMemberAsync(unitId, humanGuid, role, ct);
                Console.WriteLine(
                    $"Removed team-role membership for human '{humanGuid:N}' / role '{role}' on unit '{unitArgVal}'.");
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
}
