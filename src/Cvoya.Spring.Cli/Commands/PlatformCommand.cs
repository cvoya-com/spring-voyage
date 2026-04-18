// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Builds the <c>spring platform</c> command tree. Exposes read-only
/// platform metadata so CLI users can check what version / build they
/// are pointing at without opening the portal. Mirrors the Settings →
/// About panel on the portal (#451 — PR-S1 Sub-PR D) — both surfaces
/// hit <c>GET /api/v1/platform/info</c>.
/// </summary>
public static class PlatformCommand
{
    private static readonly OutputFormatter.Column<PlatformInfoResponse>[] InfoColumns =
    {
        new("version", r => r.Version),
        new("buildHash", r => r.BuildHash),
        new("license", r => r.License),
    };

    /// <summary>
    /// Creates the <c>platform</c> command with its read-only <c>info</c> verb.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var platformCommand = new Command("platform", "Read platform metadata (version, build hash, license).");

        platformCommand.Subcommands.Add(CreateInfoCommand(outputOption));

        return platformCommand;
    }

    private static Command CreateInfoCommand(Option<string> outputOption)
    {
        var command = new Command("info", "Print platform version, build hash, and license reference.");

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            var result = await client.GetPlatformInfoAsync(ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, InfoColumns));
        });

        return command;
    }
}