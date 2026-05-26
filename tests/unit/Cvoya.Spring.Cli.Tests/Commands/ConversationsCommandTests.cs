// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="ConversationsCommand"/> (#2787) — parses the
/// <c>spring conversations list</c> and <c>spring conversations show</c>
/// verbs. The HTTP-level behaviour is covered in
/// <see cref="ObservationEndpointsTests"/> on the API side; these tests
/// only verify the command-tree structure so flag renames break CI.
/// </summary>
public class ConversationsCommandTests
{
    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static Option<string> CreateOutputOption() =>
        new Option<string>("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
            Recursive = true,
        };

    private static (RootCommand Root, Command Conversations) BuildCommandTree()
    {
        var outputOption = CreateOutputOption();
        var conversations = ConversationsCommand.Create(outputOption);
        var root = new RootCommand { Options = { outputOption } };
        root.Subcommands.Add(conversations);
        return (root, conversations);
    }

    // -----------------------------------------------------------------------
    // Parse tests — conversations list
    // -----------------------------------------------------------------------

    [Fact]
    public void ConversationsList_NoFlags_ParsesCleanly()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("conversations list");
        result.Errors.ShouldBeEmpty();
    }

    [Fact]
    public void ConversationsList_UnitFlag_ParsesUnitValue()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("conversations list --unit engineering-team");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("--unit").ShouldBe("engineering-team");
    }

    [Fact]
    public void ConversationsList_AgentFlag_ParsesAgentValue()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("conversations list --agent ada");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("--agent").ShouldBe("ada");
    }

    [Fact]
    public void ConversationsList_ParticipantFlag_ParsesParticipantAddress()
    {
        const string participant = "human:8c5fab2a8e7e4b9c92f1d8a3b4c5d6e7";
        var (root, _) = BuildCommandTree();
        var result = root.Parse($"conversations list --participant {participant}");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("--participant").ShouldBe(participant);
    }

    [Fact]
    public void ConversationsList_LimitFlag_ParsesLimit()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("conversations list --limit 25");
        result.Errors.ShouldBeEmpty();
        result.GetValue<int?>("--limit").ShouldBe(25);
    }

    [Fact]
    public void ConversationsList_JsonOutput_ParsesCleanly()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("conversations list --output json");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("--output").ShouldBe("json");
    }

    // -----------------------------------------------------------------------
    // #2790 — keyword + recency + archived flags
    // -----------------------------------------------------------------------

    [Fact]
    public void ConversationsList_SearchFlag_ParsesSearchTerm()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("conversations list --search migration");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("--search").ShouldBe("migration");
    }

    [Fact]
    public void ConversationsList_SinceFlag_ParsesIsoInstant()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("conversations list --since 2026-05-01T00:00:00Z");
        result.Errors.ShouldBeEmpty();
        result.GetValue<DateTimeOffset?>("--since").ShouldBe(
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void ConversationsList_SinceFlag_ParsesDateOnly()
    {
        // The CLI flag mirrors the API: any ISO-8601 form parses.
        var (root, _) = BuildCommandTree();
        var result = root.Parse("conversations list --since 2026-05-01");
        result.Errors.ShouldBeEmpty();
        result.GetValue<DateTimeOffset?>("--since").ShouldNotBeNull();
    }

    [Fact]
    public void ConversationsList_ArchivedFlag_ParsesAsTrue()
    {
        var (root, _) = BuildCommandTree();
        var result = root.Parse("conversations list --archived");
        result.Errors.ShouldBeEmpty();
        result.GetValue<bool?>("--archived").ShouldBe(true);
    }

    [Fact]
    public void ConversationsList_PolishFlags_ComposeWithExistingFlags()
    {
        // Regression guard: --search + --since + --archived stay
        // independent from --unit / --agent / --participant / --limit.
        var (root, _) = BuildCommandTree();
        var result = root.Parse(
            "conversations list --unit eng-team --search migration --since 2026-05-01 --archived --limit 25");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("--unit").ShouldBe("eng-team");
        result.GetValue<string>("--search").ShouldBe("migration");
        result.GetValue<DateTimeOffset?>("--since").ShouldNotBeNull();
        result.GetValue<bool?>("--archived").ShouldBe(true);
        result.GetValue<int?>("--limit").ShouldBe(25);
    }

    // -----------------------------------------------------------------------
    // Parse tests — conversations show
    // -----------------------------------------------------------------------

    [Fact]
    public void ConversationsShow_WithId_ParsesId()
    {
        const string threadId = "a1b2c3d4-0000-0000-0000-000000000001";
        var (root, _) = BuildCommandTree();
        var result = root.Parse($"conversations show {threadId}");
        result.Errors.ShouldBeEmpty();
        result.GetValue<string>("id").ShouldBe(threadId);
    }

    [Fact]
    public void ConversationsShow_MissingId_ReportsError()
    {
        // System.CommandLine surfaces a parse error when a required positional
        // argument is missing. Verify the surface so a refactor that drops
        // the argument trips a test rather than a silent runtime crash.
        var (root, _) = BuildCommandTree();
        var result = root.Parse("conversations show");
        result.Errors.ShouldNotBeEmpty();
    }

    // -----------------------------------------------------------------------
    // Structural assertion — the read-only verb contract
    // -----------------------------------------------------------------------

    [Fact]
    public void Conversations_DoesNotExposeSendOrAnswerVerbs()
    {
        // The whole point of `conversations` vs `engagement` is read-only
        // observation. Re-introducing `send` or `answer` here would let
        // someone with TenantObserver-only roles send via the CLI surface
        // (in cloud) — the API-side gate still holds, but the CLI verb
        // would mislead. Keep the verb set minimal.
        var (_, conversations) = BuildCommandTree();
        var subcommandNames = conversations.Subcommands.Select(c => c.Name).ToList();

        subcommandNames.ShouldContain("list");
        subcommandNames.ShouldContain("show");
        subcommandNames.ShouldNotContain("send");
        subcommandNames.ShouldNotContain("answer");
    }
}
