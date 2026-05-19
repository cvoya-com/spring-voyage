// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Output;
using Cvoya.Spring.Cli.Utilities;

/// <summary>
/// Builds the <c>spring human</c> verb tree. Covers the Human × Config ×
/// General editing surface (ADR-0046 §7): <c>spring human set
/// --display-name … --description …</c>. Mirrors the
/// <c>spring agent set</c> / <c>spring unit set</c> verb shape so callers
/// do not have to memorise a different verb name per kind.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0047 §§ 2, 12 connector-native identity moves to the
/// <c>TenantUser</c> principal. The pre-ADR-0047
/// <c>spring human identity {set,list,remove}</c> verbs are deleted; Phase G
/// of the umbrella adds <c>spring user identity {set,list,remove}</c>
/// pointing at the new <c>/api/v1/tenant/users/{id}/identities</c>
/// routes. v0.1 is the freezing release — no shim verb forwards from the
/// retired surface.
/// </para>
/// <para>
/// All routes go through the Kiota-generated <c>SpringApiClient</c>; the
/// CLI never opens raw HTTP. When <c>--id</c> is omitted, the verb
/// defaults to the authenticated caller's UUID (resolved via
/// <c>GET /api/v1/tenant/auth/me</c>).
/// </para>
/// </remarks>
public static class HumanCommand
{
    /// <summary>
    /// Entry point. Returns the <c>human</c> command attached to the root.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var command = new Command(
            "human",
            "Manage human-scoped configuration (display name, description). Connector-native " +
            "identity moved to 'spring user identity ...' per ADR-0047 (Phase G of the umbrella).");

        command.Subcommands.Add(CreateHumanSetCommand(outputOption));

        // #2492: `spring human tail <id>` — humans are activity subjects
        // (messages sent / received, notifications dispatched).
        command.Subcommands.Add(ActivityTailCommand.CreateHumanTail());

        return command;
    }

    private static Command CreateHumanSetCommand(Option<string> outputOption)
    {
        var idOption = new Option<string?>("--id")
        {
            Description = "Stable human UUID. Defaults to the authenticated caller when omitted.",
        };
        var displayNameOption = new Option<string?>("--display-name")
        {
            Description = "New human-readable display name. Omit to leave the existing value untouched.",
        };
        var descriptionOption = new Option<string?>("--description")
        {
            Description =
                "New free-form description. Omit to leave unchanged. Pass an empty string \"\" to clear.",
        };

        var command = new Command(
            "set",
            "Update a human's editable identity fields (ADR-0046 §7). At least one of " +
            "--display-name / --description must be supplied; omitted flags leave the existing " +
            "value untouched.");
        command.Options.Add(idOption);
        command.Options.Add(displayNameOption);
        command.Options.Add(descriptionOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idArg = parseResult.GetValue(idOption);
            var displayName = parseResult.GetValue(displayNameOption);
            var description = parseResult.GetValue(descriptionOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            var displayNameSupplied = parseResult.GetResult(displayNameOption) is not null;
            var descriptionSupplied = parseResult.GetResult(descriptionOption) is not null;

            if (!displayNameSupplied && !descriptionSupplied)
            {
                await Console.Error.WriteLineAsync(
                    "Nothing to set. Pass at least one of --display-name or --description.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();

            Guid humanId;
            try
            {
                humanId = await ResolveHumanIdAsync(client, idArg, ct);
            }
            catch (InvalidOperationException ex)
            {
                await Console.Error.WriteLineAsync(ex.Message);
                Environment.Exit(1);
                return;
            }

            try
            {
                // Backend treats null as "leave unchanged" and an explicit
                // empty string on description as "clear" (HumanIdentity-
                // Endpoints.UpdateHumanAsync). Send empty literal when the
                // user supplied the flag with no value, null when the flag
                // was omitted entirely.
                var response = await client.UpdateHumanAsync(
                    humanId,
                    displayName: displayNameSupplied ? (displayName ?? string.Empty) : null,
                    description: descriptionSupplied ? (description ?? string.Empty) : null,
                    ct: ct);

                if (response is null)
                {
                    await Console.Error.WriteLineAsync(
                        $"Server returned no body when updating human '{humanId:N}'.");
                    Environment.Exit(1);
                    return;
                }

                if (output == "json")
                {
                    Console.WriteLine(OutputFormatter.FormatJson(response));
                }
                else
                {
                    Console.WriteLine($"Human '{humanId:N}' updated.");
                }
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await Console.Error.WriteLineAsync(
                    $"Failed to update human '{humanId:N}': {ProblemDetailsTranslator.Format(ex)}");
                Environment.Exit(1);
            }
        });

        return command;
    }

    /// <summary>
    /// Resolves the <c>--human</c> CLI option, accepting any standard Guid
    /// form (lenient parse per identifiers.md § 4) or — when omitted — the
    /// authenticated caller's stable UUID.
    /// </summary>
    private static async Task<Guid> ResolveHumanIdAsync(
        SpringApiClient client,
        string? humanArg,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(humanArg))
        {
            if (!Guid.TryParse(humanArg, out var parsed))
            {
                throw new InvalidOperationException(
                    $"--human '{humanArg}' is not a valid Guid. Use the no-dash hex (32 chars) or dashed Guid form.");
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
}
