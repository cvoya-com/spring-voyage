// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;
using System.Net;
using System.Text.Json;

using Cvoya.Spring.Cli;
using Cvoya.Spring.Cli.Commands;
using Cvoya.Spring.Cli.Generated.Models;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the <c>spring {agent,unit} skills</c> verb-tree (#2361).
///
/// Three layers:
/// <list type="number">
///   <item>
///     Parse tests — each verb registers under both subjects with the
///     expected argument + option shape and parses without errors.
///   </item>
///   <item>
///     Coordinate-parser tests (#2902) — <see cref="SkillsCommand.TryParseCoordinate"/>
///     and <see cref="SkillsCommand.TryParseCoordinateList"/> assert the real
///     split / reject / dedupe logic (multi-segment packages, malformed
///     rejection, the empty-string clear form).
///   </item>
///   <item>
///     Diff/apply tests (#2902) — <see cref="SkillsCommand.ComputeSetPlan"/>
///     is the pure <c>set</c> diff (remove-dropped, re-assert-targets-in-order);
///     <see cref="SkillsCommand.ApplySetAsync"/> wires that plan to the
///     generated client and is exercised end-to-end against a recording
///     <c>HttpMessageHandler</c> so the unequip-then-equip ordering and the
///     DELETE/POST verbs are asserted on the wire.
///   </item>
/// </list>
///
/// Surfaced by the #2890 test-integrity audit: the prior suite was parse-only,
/// so a regression in the diff (dropping the remove step, reordering the adds)
/// would have shipped green. See <c>docs/developer/test-integrity-audit.md</c>.
/// </summary>
public class SkillsCommandTests
{
    private const string BaseUrl = "http://localhost:5000";

    // ----- Agent verbs --------------------------------------------------

    [Fact]
    public void AgentSkillsList_Parses()
    {
        var parseResult = ParseAgent("agent skills list ada");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("agent").ShouldBe("ada");
    }

    [Fact]
    public void AgentSkillsAdd_RequiresSkillOption()
    {
        var parseResult = ParseAgent("agent skills add ada");
        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentSkillsAdd_ParsesSkillFlag()
    {
        var parseResult = ParseAgent(
            "agent skills add ada --skill spring-voyage/software-engineering/code-review");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("agent").ShouldBe("ada");
        parseResult.GetValue<string>("--skill")
            .ShouldBe("spring-voyage/software-engineering/code-review");
    }

    [Fact]
    public void AgentSkillsRemove_RequiresSkillOption()
    {
        var parseResult = ParseAgent("agent skills remove ada");
        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentSkillsRemove_ParsesSkillFlag()
    {
        var parseResult = ParseAgent(
            "agent skills remove ada --skill spring-voyage/software-engineering/code-review");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--skill")
            .ShouldBe("spring-voyage/software-engineering/code-review");
    }

    [Fact]
    public void AgentSkillsSet_RequiresSkillsOption()
    {
        var parseResult = ParseAgent("agent skills set ada");
        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void AgentSkillsSet_ParsesCommaSeparatedSkills()
    {
        var parseResult = ParseAgent(
            "agent skills set ada --skills a/x,b/y");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--skills").ShouldBe("a/x,b/y");
    }

    [Fact]
    public void AgentSkillsSet_AcceptsEmptyStringForClear()
    {
        // The 'set' verb accepts --skills="" as the canonical clear-all form.
        var parseResult = ParseAgent("agent skills set ada --skills \"\"");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--skills").ShouldBe(string.Empty);
    }

    // ----- Unit verbs ---------------------------------------------------

    [Fact]
    public void UnitSkillsList_Parses()
    {
        var parseResult = ParseUnit("unit skills list engineering");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("engineering");
    }

    [Fact]
    public void UnitSkillsAdd_RequiresSkillOption()
    {
        var parseResult = ParseUnit("unit skills add engineering");
        parseResult.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void UnitSkillsAdd_ParsesSkillFlag()
    {
        var parseResult = ParseUnit(
            "unit skills add engineering --skill spring-voyage/software-engineering/code-review");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("unit").ShouldBe("engineering");
        parseResult.GetValue<string>("--skill")
            .ShouldBe("spring-voyage/software-engineering/code-review");
    }

    [Fact]
    public void UnitSkillsRemove_ParsesSkillFlag()
    {
        var parseResult = ParseUnit(
            "unit skills remove engineering --skill spring-voyage/software-engineering/code-review");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--skill")
            .ShouldBe("spring-voyage/software-engineering/code-review");
    }

    [Fact]
    public void UnitSkillsSet_ParsesCommaSeparatedSkills()
    {
        var parseResult = ParseUnit(
            "unit skills set engineering --skills a/x,b/y,c/z");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--skills").ShouldBe("a/x,b/y,c/z");
    }

    // ----- Output flag --------------------------------------------------

    [Fact]
    public void AgentSkillsList_AcceptsJsonOutput()
    {
        // --output is a root-level recursive option, mirroring how
        // MemoryCommandTests asserts it carries down to leaf verbs.
        var parseResult = ParseAgent("--output json agent skills list ada");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--output").ShouldBe("json");
    }

    [Fact]
    public void UnitSkillsList_AcceptsJsonOutput()
    {
        var parseResult = ParseUnit("--output json unit skills list engineering");
        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string>("--output").ShouldBe("json");
    }

    // ----- Coordinate parser: single (#2902) ---------------------------

    [Theory]
    [InlineData("a/x", "a", "x")]
    [InlineData("  a/x  ", "a", "x")] // outer whitespace is trimmed
    // The package may itself contain slashes — the skill is always the final
    // segment, so the split is from the right.
    [InlineData("spring-voyage/software-engineering/code-review", "spring-voyage/software-engineering", "code-review")]
    [InlineData("a/b/c/d", "a/b/c", "d")]
    public void TryParseCoordinate_Valid_SplitsOnLastSlash(string raw, string expectedPkg, string expectedSkill)
    {
        var ok = SkillsCommand.TryParseCoordinate(raw, out var pkg, out var skill, out var error);

        ok.ShouldBeTrue();
        pkg.ShouldBe(expectedPkg);
        skill.ShouldBe(expectedSkill);
        error.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("")]            // empty
    [InlineData("   ")]         // whitespace-only
    [InlineData("nopkg")]       // no separator
    [InlineData("/x")]          // empty package (leading slash)
    [InlineData("a/")]          // empty skill (trailing slash)
    [InlineData("/")]           // both empty
    public void TryParseCoordinate_Malformed_Rejected(string raw)
    {
        var ok = SkillsCommand.TryParseCoordinate(raw, out var pkg, out var skill, out var error);

        ok.ShouldBeFalse();
        // Out params are reset, not left holding a half-parsed value.
        pkg.ShouldBeEmpty();
        skill.ShouldBeEmpty();
        error.ShouldNotBeEmpty();
    }

    // ----- Coordinate parser: list (#2902) ------------------------------

    [Fact]
    public void TryParseCoordinateList_EmptyString_IsTheClearForm()
    {
        // --skills="" is the canonical clear-all form: parses to an empty
        // target list (not an error), which ApplySetAsync turns into "remove
        // everything, add nothing".
        var ok = SkillsCommand.TryParseCoordinateList(string.Empty, out var targets, out var error);

        ok.ShouldBeTrue();
        targets.ShouldBeEmpty();
        error.ShouldBeEmpty();
    }

    [Fact]
    public void TryParseCoordinateList_PreservesOperatorOrder()
    {
        var ok = SkillsCommand.TryParseCoordinateList("c/z,a/x,b/y", out var targets, out var error);

        ok.ShouldBeTrue();
        error.ShouldBeEmpty();
        // Order is significant — it becomes the persisted declaration order.
        targets.ShouldBe(new[] { ("c", "z"), ("a", "x"), ("b", "y") });
    }

    [Fact]
    public void TryParseCoordinateList_MultiSegmentPackages_SplitPerToken()
    {
        var ok = SkillsCommand.TryParseCoordinateList(
            "spring-voyage/se/code-review,other-pkg/lint", out var targets, out var error);

        ok.ShouldBeTrue();
        error.ShouldBeEmpty();
        targets.ShouldBe(new[] { ("spring-voyage/se", "code-review"), ("other-pkg", "lint") });
    }

    [Fact]
    public void TryParseCoordinateList_SkipsEmptyEntries()
    {
        // Trailing / doubled commas and post-comma whitespace are tolerated —
        // RemoveEmptyEntries drops the gaps and each token is trimmed.
        var ok = SkillsCommand.TryParseCoordinateList("a/x, b/y,,c/z,", out var targets, out var error);

        ok.ShouldBeTrue();
        error.ShouldBeEmpty();
        targets.ShouldBe(new[] { ("a", "x"), ("b", "y"), ("c", "z") });
    }

    [Fact]
    public void TryParseCoordinateList_Duplicate_Rejected()
    {
        var ok = SkillsCommand.TryParseCoordinateList("a/x,b/y,a/x", out var targets, out var error);

        ok.ShouldBeFalse();
        error.ShouldContain("duplicate");
        error.ShouldContain("a/x");
    }

    [Fact]
    public void TryParseCoordinateList_MalformedToken_Rejected()
    {
        // One bad token fails the whole list — the inner parser's error is
        // surfaced, not swallowed.
        var ok = SkillsCommand.TryParseCoordinateList("a/x,not-a-coordinate", out var targets, out var error);

        ok.ShouldBeFalse();
        error.ShouldNotBeEmpty();
    }

    // ----- ComputeSetPlan: the pure diff (#2902) ------------------------

    [Fact]
    public void ComputeSetPlan_AddOnly_NoRemoves()
    {
        var plan = SkillsCommand.ComputeSetPlan(
            current: Equipped(),
            targets: new[] { ("a", "x"), ("b", "y") });

        plan.Removes.ShouldBeEmpty();
        plan.Adds.ShouldBe(new[] { ("a", "x"), ("b", "y") });
    }

    [Fact]
    public void ComputeSetPlan_EmptyTargets_RemovesEverything_AddsNothing()
    {
        // The clear form: every equipped bundle is dropped, in current order.
        var plan = SkillsCommand.ComputeSetPlan(
            current: Equipped(("a", "x"), ("b", "y")),
            targets: Array.Empty<(string, string)>());

        plan.Removes.ShouldBe(new[] { ("a", "x"), ("b", "y") });
        plan.Adds.ShouldBeEmpty();
    }

    [Fact]
    public void ComputeSetPlan_AllKept_ReAssertsTargets_NoRemoves()
    {
        // The "no-op" set: target == current. Nothing is removed, but every
        // bundle is re-asserted (POST is the store's add-then-refresh path),
        // so a regression that skipped kept bundles would surface here.
        var plan = SkillsCommand.ComputeSetPlan(
            current: Equipped(("a", "x"), ("b", "y")),
            targets: new[] { ("a", "x"), ("b", "y") });

        plan.Removes.ShouldBeEmpty();
        plan.Adds.ShouldBe(new[] { ("a", "x"), ("b", "y") });
    }

    [Fact]
    public void ComputeSetPlan_Mixed_RemovesDropped_AddsTargets()
    {
        var plan = SkillsCommand.ComputeSetPlan(
            current: Equipped(("a", "x"), ("b", "y")),
            targets: new[] { ("b", "y"), ("c", "z") });

        plan.Removes.ShouldBe(new[] { ("a", "x") });       // dropped
        plan.Adds.ShouldBe(new[] { ("b", "y"), ("c", "z") }); // kept + new
    }

    [Fact]
    public void ComputeSetPlan_RemovesFollowCurrentOrder_AddsFollowOperatorOrder()
    {
        // Removes are emitted in the server's current order; adds are emitted
        // in the operator's flag order — independent of how current is ordered.
        var plan = SkillsCommand.ComputeSetPlan(
            current: Equipped(("a", "x"), ("b", "y"), ("c", "z")),
            targets: new[] { ("c", "z"), ("a", "x") });

        plan.Removes.ShouldBe(new[] { ("b", "y") });
        plan.Adds.ShouldBe(new[] { ("c", "z"), ("a", "x") }); // operator order, not current
    }

    [Fact]
    public void ComputeSetPlan_MultiSegmentPackages_KeyMatchRoundTrips()
    {
        // A kept multi-segment-package bundle must NOT be treated as dropped —
        // the (package, skill) key has to round-trip through the diff.
        var plan = SkillsCommand.ComputeSetPlan(
            current: Equipped(("spring-voyage/se", "code-review"), ("legacy/pkg", "lint")),
            targets: new[] { ("spring-voyage/se", "code-review") });

        plan.Removes.ShouldBe(new[] { ("legacy/pkg", "lint") });
        plan.Adds.ShouldBe(new[] { ("spring-voyage/se", "code-review") });
    }

    // ----- ApplySetAsync: composition on the wire (#2902) ---------------

    [Fact]
    public async Task ApplySetAsync_Agent_Mixed_UnequipsDroppedThenEquipsTargetsInOrder()
    {
        // current = a/x, b/y ; target = b/y, c/z  →  DELETE a/x, then POST b/y, POST c/z.
        var handler = new SkillsSetRecordingHandler(SkillsBody(("a", "x"), ("b", "y")));
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        await SkillsCommand.ApplySetAsync(
            client, "ada",
            new[] { ("b", "y"), ("c", "z") },
            agent: true,
            TestContext.Current.CancellationToken);

        // The current list is fetched first, against the agent path.
        handler.Calls[0].Method.ShouldBe(HttpMethod.Get);
        handler.Calls[0].Path.ShouldBe("/api/v1/tenant/agents/ada/skills");

        handler.Deletes.ShouldBe(new[] { ("a", "x") });            // only the dropped bundle
        handler.Posts.ShouldBe(new[] { ("b", "y"), ("c", "z") });  // targets, operator order

        // Unequips strictly precede equips on the wire (the doc-commented
        // invariant: a re-ordered-but-kept bundle can't 400 a pending equip).
        handler.LastDeleteIndex.ShouldBeLessThan(handler.FirstPostIndex);
    }

    [Fact]
    public async Task ApplySetAsync_Agent_Clear_DeletesEverything_PostsNothing()
    {
        // Falsifiability for "dropping the remove step": clearing must DELETE
        // every equipped bundle and POST none.
        var handler = new SkillsSetRecordingHandler(SkillsBody(("a", "x"), ("b", "y")));
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        await SkillsCommand.ApplySetAsync(
            client, "ada",
            Array.Empty<(string, string)>(),
            agent: true,
            TestContext.Current.CancellationToken);

        handler.Deletes.ShouldBe(new[] { ("a", "x"), ("b", "y") });
        handler.Posts.ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplySetAsync_Unit_KeptBundleReAssertedNotDeleted_HitsUnitPath()
    {
        // current = a/x ; target = a/x, b/y. a/x is kept → re-POSTed, never
        // DELETEd. Also pins the unit (not agent) endpoint path.
        var handler = new SkillsSetRecordingHandler(SkillsBody(("a", "x")));
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        await SkillsCommand.ApplySetAsync(
            client, "engineering",
            new[] { ("a", "x"), ("b", "y") },
            agent: false,
            TestContext.Current.CancellationToken);

        handler.Calls[0].Path.ShouldBe("/api/v1/tenant/units/engineering/skills");
        handler.Deletes.ShouldBeEmpty();                            // a/x kept, not dropped
        handler.Posts.ShouldBe(new[] { ("a", "x"), ("b", "y") });
    }

    // ----- Plumbing -----------------------------------------------------

    /// <summary>Builds the equipped-skills GET response body for the recording handler.</summary>
    private static string SkillsBody(params (string Package, string Skill)[] entries)
        => JsonSerializer.Serialize(new
        {
            skills = entries
                .Select(e => new { packageName = e.Package, skillName = e.Skill, promptSummary = string.Empty })
                .ToArray(),
        });

    /// <summary>Builds the <see cref="EquippedSkillEntry"/> list for a <see cref="SkillsCommand.ComputeSetPlan"/> call.</summary>
    private static List<EquippedSkillEntry> Equipped(params (string Package, string Skill)[] entries)
        => entries
            .Select(e => new EquippedSkillEntry { PackageName = e.Package, SkillName = e.Skill })
            .ToList();

    private static ParseResult ParseAgent(string commandLine)
    {
        var outputOption = OutputOption();
        var agentCommand = AgentCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(agentCommand);
        return rootCommand.Parse(commandLine);
    }

    private static ParseResult ParseUnit(string commandLine)
    {
        var outputOption = OutputOption();
        var unitCommand = UnitCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(unitCommand);
        return rootCommand.Parse(commandLine);
    }

    private static Option<string> OutputOption() =>
        new("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
            Recursive = true,
        };

    /// <summary>
    /// Records the sequence of HTTP calls <see cref="SkillsCommand.ApplySetAsync"/>
    /// makes so the test can assert the unequip-then-equip order and the
    /// DELETE/POST verbs on the wire. The first GET returns the configured
    /// current list (the diff is computed from it); every other call gets a
    /// benign empty body — its response only feeds the final printed list,
    /// which these tests don't inspect.
    /// </summary>
    private sealed class SkillsSetRecordingHandler(string currentSkillsBody) : HttpMessageHandler
    {
        public List<RecordedCall> Calls { get; } = new();

        /// <summary>(package, skill) of each DELETE, decoded from the path, in call order.</summary>
        public IReadOnlyList<(string Package, string Skill)> Deletes =>
            Calls.Where(c => c.Method == HttpMethod.Delete).Select(c => DecodePath(c.Path)).ToList();

        /// <summary>(package, skill) of each POST, decoded from the body, in call order.</summary>
        public IReadOnlyList<(string Package, string Skill)> Posts =>
            Calls.Where(c => c.Method == HttpMethod.Post).Select(c => DecodeBody(c.Body)).ToList();

        public int LastDeleteIndex => Calls.FindLastIndex(c => c.Method == HttpMethod.Delete);

        public int FirstPostIndex => Calls.FindIndex(c => c.Method == HttpMethod.Post);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add(new RecordedCall(request.Method, request.RequestUri!.AbsolutePath, body));

            var responseBody = request.Method == HttpMethod.Get ? currentSkillsBody : """{"skills":[]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, System.Text.Encoding.UTF8, "application/json"),
            };
        }

        // DELETE path: /api/v1/tenant/{agents|units}/{id}/skills/{enc-pkg}/{skill}.
        // The package is one url-encoded segment (slashes → %2F); the skill is
        // the final segment, so split the suffix on its last '/'.
        private static (string Package, string Skill) DecodePath(string path)
        {
            const string marker = "/skills/";
            var rest = path[(path.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..];
            var lastSlash = rest.LastIndexOf('/');
            return (Uri.UnescapeDataString(rest[..lastSlash]), rest[(lastSlash + 1)..]);
        }

        private static (string Package, string Skill) DecodeBody(string? body)
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body!);
            return (json.GetProperty("packageName").GetString()!, json.GetProperty("skillName").GetString()!);
        }

        public sealed record RecordedCall(HttpMethod Method, string Path, string? Body);
    }
}
