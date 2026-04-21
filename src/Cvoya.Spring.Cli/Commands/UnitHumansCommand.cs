// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;
using Cvoya.Spring.Cli.Utilities;

/// <summary>
/// Builds the <c>spring unit humans add|remove|list</c> subtree (#454).
/// Target surface is the server's
/// <c>PATCH / DELETE /api/v1/units/{id}/humans/{humanId}/permissions</c>
/// and <c>GET /api/v1/units/{id}/humans</c>. Role enforcement lives on the
/// server — the endpoints are gated by <c>PermissionPolicies.UnitOwner</c>
/// (add / remove) and <c>PermissionPolicies.UnitViewer</c> (list), and the
/// CLI surfaces the authorization failure verbatim instead of re-checking
/// locally.
/// </summary>
/// <remarks>
/// <para>
/// The shape of <c>add</c> matches the wording used in the Observing guide
/// verbatim: <c>spring unit humans add &lt;unit&gt; &lt;identity&gt; --permission
/// owner|operator|viewer --notifications slack,email</c>. The
/// <c>--notifications</c> flag takes a free-form string value: the wire
/// type is a bool, so any non-empty token (channel list, <c>true</c>,
/// <c>on</c>) maps to <c>true</c>, and <c>false</c> / <c>off</c> /
/// <c>none</c> maps to <c>false</c>. Channel-specific notification routing
/// is future work (tracked in the observing guide).
/// </para>
/// <para>
/// <c>humans remove</c> maps to DELETE. The server is idempotent (removing
/// an entry that does not exist still returns 204), so the CLI prints a
/// success message regardless of prior presence.
/// </para>
/// </remarks>
public static class UnitHumansCommand
{
    private static readonly OutputFormatter.Column<UnitPermissionEntry>[] HumanColumns =
    {
        new("humanId", e => e.HumanId),
        new("permission", e => e.Permission?.ToString()),
        new("identity", e => e.Identity),
        new("notifications", e => e.Notifications?.ToString().ToLowerInvariant()),
    };

    /// <summary>
    /// Entry point. Returns the <c>humans</c> subcommand tree for attachment
    /// under <c>unit</c>.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var command = new Command(
            "humans",
            "Manage the humans (owners / operators / viewers) associated with a unit.");

        command.Subcommands.Add(CreateAddCommand(outputOption));
        command.Subcommands.Add(CreateRemoveCommand());
        command.Subcommands.Add(CreateListCommand(outputOption));
        return command;
    }

    private static Command CreateAddCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var identityArg = new Argument<string>("identity")
        {
            Description =
                "The human identifier (usually a username or email). Used as both the actor id " +
                "and the default display identity when --identity is omitted.",
        };
        var permissionOption = new Option<string>("--permission")
        {
            Description = "Permission level (owner, operator, viewer).",
            Required = true,
        };
        permissionOption.AcceptOnlyFromAmong("owner", "operator", "viewer");
        var displayIdentityOption = new Option<string?>("--identity")
        {
            Description =
                "Optional display identity override (e.g. an email when the positional argument is a username).",
        };
        var notificationsOption = new Option<string?>("--notifications")
        {
            Description =
                "Notification routing — accepts 'true'/'false' or a comma-separated channel list " +
                "(slack,email). Any non-empty channel list enables notifications; 'false' or 'none' disables them.",
        };

        var command = new Command(
            "add",
            "Grant or update a human's permission on this unit. Idempotent — re-running with a " +
            "different --permission updates the existing entry in place.");
        command.Arguments.Add(unitArg);
        command.Arguments.Add(identityArg);
        command.Options.Add(permissionOption);
        command.Options.Add(displayIdentityOption);
        command.Options.Add(notificationsOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var humanId = parseResult.GetValue(identityArg)!;
            var permission = parseResult.GetValue(permissionOption)!;
            var identity = parseResult.GetValue(displayIdentityOption);
            var notificationsRaw = parseResult.GetValue(notificationsOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            try
            {
                var response = await client.SetUnitHumanPermissionAsync(
                    unitId,
                    humanId,
                    permission: permission,
                    identity: identity,
                    notifications: ParseNotifications(notificationsRaw),
                    ct: ct);

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(response));
                }
                else
                {
                    Console.WriteLine(
                        $"Human '{response.HumanId}' granted '{response.Permission}' permission on unit '{unitId}'.");
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                // The endpoint is Owner-gated; surface the server's verbatim
                // error so an unauthorised operator / viewer sees the
                // authorization failure, not a generic Kiota error.
                await Console.Error.WriteLineAsync(
                    $"Failed to set permission for human '{humanId}' on unit '{unitId}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateRemoveCommand()
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var identityArg = new Argument<string>("identity")
        {
            Description = "The human identifier to remove from the unit.",
        };

        var command = new Command(
            "remove",
            "Remove a human's permission entry from this unit. Idempotent — completes successfully " +
            "whether or not the human previously had an entry.");
        command.Arguments.Add(unitArg);
        command.Arguments.Add(identityArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var humanId = parseResult.GetValue(identityArg)!;
            var client = ClientFactory.Create();

            try
            {
                await client.RemoveUnitHumanPermissionAsync(unitId, humanId, ct);
                Console.WriteLine($"Human '{humanId}' removed from unit '{unitId}'.");
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to remove human '{humanId}' from unit '{unitId}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateListCommand(Option<string> outputOption)
    {
        var unitArg = new Argument<string>("unit") { Description = "The unit identifier" };
        var command = new Command(
            "list",
            "List every human who has a permission entry on this unit.");
        command.Arguments.Add(unitArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var unitId = parseResult.GetValue(unitArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            try
            {
                var entries = await client.ListUnitHumanPermissionsAsync(unitId, ct);

                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(entries)
                    : OutputFormatter.FormatTable(entries, HumanColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                // The GET endpoint is Viewer-gated — an unauthenticated
                // caller sees 401 / 403 here, which is exactly the failure
                // mode the "unauthorised viewer" test case exercises.
                await Console.Error.WriteLineAsync(
                    $"Failed to list humans for unit '{unitId}': {ProblemDetailsFormatter.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    /// <summary>
    /// Maps the free-form <c>--notifications</c> flag to the boolean the
    /// server currently accepts. Documented flag style is a comma-separated
    /// channel list (<c>slack,email</c>); any non-empty list means "enabled".
    /// Explicit <c>false</c> / <c>off</c> / <c>none</c> (case-insensitive)
    /// map to <c>false</c>. <c>null</c> / empty leaves the server-side value
    /// untouched.
    /// </summary>
    public static bool? ParseNotifications(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var normalised = value.Trim();
        if (bool.TryParse(normalised, out var asBool))
        {
            return asBool;
        }
        if (string.Equals(normalised, "off", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalised, "none", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalised, "disabled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // Anything else — including a comma-separated channel list — means
        // "notifications are enabled, channel routing is future work".
        return true;
    }
}