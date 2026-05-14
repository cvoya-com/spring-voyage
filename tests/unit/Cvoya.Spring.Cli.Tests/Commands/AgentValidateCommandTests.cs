// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using Cvoya.Spring.Cli.Commands;
using Cvoya.Spring.Cli.Generated.Models;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AgentValidateCommand"/> (#2096 / ADR-0041).
/// Exercise <c>CollectFindings</c> directly so the surface stays
/// deterministic without requiring a live API server. The CLI wiring
/// itself is exercised by <c>CommandParsingTests</c>.
/// </summary>
public class AgentValidateCommandTests
{
    [Fact]
    public void CollectFindings_ConcurrentThreadsTrue_EmitsOptInWarning()
    {
        var execution = new AgentExecutionResponse
        {
            ConcurrentThreads = true,
        };

        var findings = AgentValidateCommand.CollectFindings(execution);

        findings.Count.ShouldBe(1);
        findings[0].Code.ShouldBe("ConcurrentThreadsOptIn");
        findings[0].Message.ShouldBe(AgentValidateCommand.ConcurrentThreadsWarningMessage);
    }

    [Fact]
    public void CollectFindings_ConcurrentThreadsFalse_NoFindings()
    {
        // Safe-default mode — no warnings emitted. The author has
        // explicitly opted out of the runtime-contract surface.
        var execution = new AgentExecutionResponse
        {
            ConcurrentThreads = false,
        };

        var findings = AgentValidateCommand.CollectFindings(execution);

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void CollectFindings_ConcurrentThreadsNull_NoFindings()
    {
        // null means the agent has no execution.concurrent_threads slot
        // declared and the dispatcher uses the runtime's record default.
        // The CLI does not warn here — the warning is for explicit opt-in,
        // not implicit defaults that the author may not even know about.
        // (Tightening the runtime default is tracked separately under
        // #2090 / ADR-0041.)
        var execution = new AgentExecutionResponse
        {
            ConcurrentThreads = null,
        };

        var findings = AgentValidateCommand.CollectFindings(execution);

        findings.ShouldBeEmpty();
    }

    [Fact]
    public void Create_RegistersValidateSubcommand_UnderAgent()
    {
        // Smoke test for the parse path: confirm `spring agent validate
        // <id>` parses without errors and exposes the expected positional
        // argument and --unit option. Wiring through the full action
        // requires a live API server (covered by integration tests, not
        // unit tests), so this test stops at parse-time validation.
        var outputOption = new System.CommandLine.Option<string>("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
            Recursive = true,
        };
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new System.CommandLine.RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);

        var parseResult = rootCommand.Parse(
            "agent validate aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa --unit engineering");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("id-or-name")
            .ShouldBe("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        parseResult.GetValue<string?>("--unit").ShouldBe("engineering");
    }

    [Fact]
    public void ConcurrentThreadsWarningMessage_PinsLoadBearingTerms()
    {
        // The warning copies the contract terms verbatim from ADR-0041
        // / docs/architecture/agent-runtime.md so authors see the same
        // language across docs, ADR, and CLI. If a future reword drops
        // one of these terms, this test fails so the deletion is
        // intentional and the docs / CLI stay in sync.
        var msg = AgentValidateCommand.ConcurrentThreadsWarningMessage;

        msg.ShouldContain("concurrent_threads: true");
        msg.ShouldContain("opt-in");
        msg.ShouldContain("fixed ports");
        msg.ShouldContain("$SPRING_WORKSPACE_PATH/threads/");
        msg.ShouldContain("pkill -f pytest");
        msg.ShouldContain("global state");
        msg.ShouldContain("pytest --watch");
        msg.ShouldContain("npm run dev");
        msg.ShouldContain("agent-runtime.md");
        msg.ShouldContain("0041-actor-runtime-contract.md");
    }
}
