// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// Implements <c>spring agent validate &lt;id&gt;</c> (#2096 / ADR-0041).
/// Reads the agent's persisted execution block and surfaces author-facing
/// warnings about runtime-contract opt-ins. The first finding shipped is
/// the <c>concurrent_threads: true</c> author contract — the agent is
/// allowed to opt in but the author owns ports, /tmp, child PIDs, and
/// shared global state inside the container.
/// </summary>
/// <remarks>
/// <para>
/// Exit code is always <c>0</c> for findings — warnings are visible but
/// non-blocking by design. Exit code is non-zero only for I/O / argument
/// errors (resolver miss, server unreachable). The verb intentionally
/// mirrors <c>spring package validate</c>'s <c>WARN</c> / <c>ERROR</c>
/// table style so operators see the same shape across the CLI.
/// </para>
/// </remarks>
public static class AgentValidateCommand
{
    /// <summary>Builds the <c>validate</c> subcommand under <c>spring agent</c>.</summary>
    public static Command Create(Option<string> outputOption)
        => Create(outputOption, () => ClientFactory.Create());

    internal static Command Create(Option<string> outputOption, Func<SpringApiClient> clientFactory)
    {
        var idArg = new Argument<string>("id-or-name")
        {
            Description =
                "The agent's stable Guid (32-char no-dash hex or dashed form), " +
                "OR a display_name to search for. When a name is supplied and " +
                "multiple agents match, a disambiguation list is printed with " +
                "each candidate's Guid.",
        };
        var unitOption = new Option<string?>("--unit")
        {
            Description =
                "Optional parent-unit context (Guid or display_name) used to " +
                "constrain a name search to that unit's members. Ignored when " +
                "the first argument is itself a Guid.",
        };

        var command = new Command(
            "validate",
            "Validate an agent's persisted execution block and surface author-facing warnings " +
            "(e.g. opt-ins to the `concurrent_threads: true` runtime contract). " +
            "Warnings are visible but non-blocking — exit code is always 0 unless lookup itself fails.");
        command.Arguments.Add(idArg);
        command.Options.Add(unitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(idArg)!;
            var unitArg = parseResult.GetValue(unitOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            var client = clientFactory();
            var resolver = new CliResolver(client);

            // Mirror `agent show`'s resolution surface so authors can pass
            // either form. The optional --unit constrains a name search.
            Guid? unitContext = null;
            if (!string.IsNullOrWhiteSpace(unitArg))
            {
                try
                {
                    unitContext = await resolver.ResolveUnitAsync(unitArg, parentContext: null, ct);
                }
                catch (CliResolutionException ex)
                {
                    CliResolutionPrinter.Write(Console.Error, ex);
                    Environment.Exit(1);
                    return;
                }
            }

            Guid agentId;
            try
            {
                agentId = await resolver.ResolveAgentAsync(idOrName, unitContext, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            var canonical = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(agentId);

            AgentExecutionResponse execution;
            try
            {
                execution = await client.GetAgentExecutionAsync(canonical, ct);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                await Console.Error.WriteLineAsync(
                    $"No agent found matching '{idOrName}'.");
                Environment.Exit(1);
                return;
            }

            var findings = CollectFindings(execution);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(new
                {
                    agent = canonical,
                    findings = findings.Select(f => new
                    {
                        severity = "warning",
                        code = f.Code,
                        message = f.Message,
                    }),
                }));
            }
            else
            {
                Console.WriteLine($"Validating agent {canonical} ...");
                if (findings.Count == 0)
                {
                    Console.WriteLine("  ok — no author-contract findings.");
                }
                else
                {
                    foreach (var f in findings)
                    {
                        Console.WriteLine($"  WARN  [{f.Code}] {f.Message}");
                    }
                }
            }
        });

        return command;
    }

    /// <summary>
    /// Walks the agent's wire-side execution snapshot and returns one
    /// <see cref="Finding"/> per author-contract opt-in. The list is
    /// stable / deterministic so JSON consumers can diff it.
    /// </summary>
    /// <remarks>
    /// New checks land here — keep them small and self-documenting. Each
    /// finding carries a stable <see cref="Finding.Code"/> so CI tooling
    /// can grep / suppress on the code, not the prose.
    /// </remarks>
    internal static IReadOnlyList<Finding> CollectFindings(AgentExecutionResponse execution)
    {
        var findings = new List<Finding>();

        if (execution.ConcurrentThreads is true)
        {
            findings.Add(new Finding(
                Code: "ConcurrentThreadsOptIn",
                Message: ConcurrentThreadsWarningMessage));
        }

        return findings;
    }

    /// <summary>
    /// Author-facing warning text emitted when an agent opts in to the
    /// <c>concurrent_threads: true</c> runtime mode. Surfaces the contract
    /// summary and the doc / ADR pointers so the author cannot opt in
    /// without seeing the constraints they signed up for.
    /// </summary>
    /// <remarks>
    /// The text is a constant (not a format string) so tests can pin it
    /// verbatim and a future reword is intentional. Doc anchors point at
    /// the agent-runtime canonical contract section (ADR-0041 is linked
    /// there as the durable record).
    /// </remarks>
    internal const string ConcurrentThreadsWarningMessage =
        "concurrent_threads: true is opt-in. The author contract requires that the agent " +
        "must NOT bind fixed ports, must NOT write outside " +
        "$SPRING_WORKSPACE_PATH/threads/<thread.id>/, must NOT assume any tool's child " +
        "processes are uniquely theirs (no `pkill -f pytest` patterns), and must NOT " +
        "mutate shared global state (env vars, working directory, signal handlers). " +
        "For CLI runtimes the system prompt MUST forbid long-running watchers " +
        "(`pytest --watch`, `npm run dev`, etc.). " +
        "See docs/architecture/agent-runtime.md (\"concurrent_threads: true author contract\") " +
        "and docs/decisions/0041-actor-runtime-contract.md.";

    /// <summary>One author-contract finding surfaced by the validator.</summary>
    /// <param name="Code">
    /// Stable identifier (e.g. <c>ConcurrentThreadsOptIn</c>). Used as
    /// the JSON-output key and as the prose-output bracket tag so CI
    /// tooling can grep / suppress on a stable token instead of the
    /// human-readable message.
    /// </param>
    /// <param name="Message">Author-facing prose explaining the opt-in / constraint.</param>
    internal sealed record Finding(string Code, string Message);
}
