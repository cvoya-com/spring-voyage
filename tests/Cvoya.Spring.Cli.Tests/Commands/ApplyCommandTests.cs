// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Cli;
using Cvoya.Spring.Cli.Commands;
using Cvoya.Spring.Manifest;

using Shouldly;

using Xunit;

public class ApplyCommandTests
{
    private const string EngineeringTeamYaml = """
        unit:
          name: engineering-team
          description: A software engineering team with a tech lead, backend engineers, and QA.
          structure: hierarchical
          ai:
            agent: claude
            model: claude-sonnet-4-20250514
            prompt: |
              You coordinate a software engineering team.
              Route incoming work to the most appropriate team member.
            skills:
              - package: spring-voyage/software-engineering
                skill: triage-and-assign
          members:
            - agent: tech-lead
            - agent: backend-engineer
            - agent: qa-engineer
          execution:
            image: spring-agent:latest
            runtime: docker
          connectors:
            - type: github
              config:
                events: ["issues", "pull_request"]
          policies:
            communication: through-unit
            work_assignment: capability-match
          humans:
            - identity: owner
              permission: owner
              notifications: ["escalation", "completion"]
        """;

    [Fact]
    public void Parse_EngineeringTeamManifest_MapsCoreFields()
    {
        var manifest = ApplyRunner.Parse(EngineeringTeamYaml);

        manifest.Name.ShouldBe("engineering-team");
        manifest.Description.ShouldStartWith("A software engineering team");
        manifest.Structure.ShouldBe("hierarchical");
        manifest.Members.ShouldNotBeNull();
        manifest.Members!.Count.ShouldBe(3);
        manifest.Members![0].Agent.ShouldBe("tech-lead");
        manifest.Members[1].Agent.ShouldBe("backend-engineer");
        manifest.Members[2].Agent.ShouldBe("qa-engineer");

        manifest.Ai.ShouldNotBeNull();
        manifest.Ai!.Agent.ShouldBe("claude");
        manifest.Ai.Model.ShouldBe("claude-sonnet-4-20250514");
        manifest.Ai.Skills.ShouldNotBeNull();
        manifest.Ai.Skills!.Count.ShouldBe(1);

        manifest.Execution.ShouldNotBeNull();
        manifest.Execution!.Image.ShouldBe("spring-agent:latest");
        manifest.Connectors.ShouldNotBeNull();
        manifest.Connectors!.Count.ShouldBe(1);
        manifest.Policies.ShouldNotBeNull();
        manifest.Policies!.ShouldContainKey("communication");
        manifest.Humans.ShouldNotBeNull();
        manifest.Humans!.Count.ShouldBe(1);
    }

    [Fact]
    public void Parse_MinimalManifest_AllOtherSectionsNull()
    {
        var yaml = "unit:\n  name: x\n";

        var manifest = ApplyRunner.Parse(yaml);

        manifest.Name.ShouldBe("x");
        manifest.Description.ShouldBeNull();
        manifest.Ai.ShouldBeNull();
        manifest.Members.ShouldBeNull();
        manifest.Connectors.ShouldBeNull();
        manifest.Policies.ShouldBeNull();
        manifest.Humans.ShouldBeNull();
        manifest.Execution.ShouldBeNull();
    }

    [Fact]
    public void Parse_MissingUnitName_Throws()
    {
        var yaml = "unit:\n  description: no name here\n";

        var act = () => ApplyRunner.Parse(yaml);

        Should.Throw<ManifestParseException>(act)
            .Message.ShouldContain("unit.name");
    }

    [Fact]
    public void Parse_MissingUnitRoot_Throws()
    {
        var yaml = "other: 1\n";

        var act = () => ApplyRunner.Parse(yaml);

        Should.Throw<ManifestParseException>(act)
            .Message.ShouldContain("'unit' root section");
    }

    [Fact]
    public void PrintPlan_DryRun_MentionsUnitAndEachMember()
    {
        var manifest = ApplyRunner.Parse(EngineeringTeamYaml);

        using var writer = new StringWriter();
        ApplyRunner.PrintPlan(manifest, writer);

        var output = writer.ToString();

        output.ShouldContain("engineering-team");
        output.ShouldContain("agent:tech-lead");
        output.ShouldContain("agent:backend-engineer");
        output.ShouldContain("agent:qa-engineer");
        output.ShouldContain("no API calls were made");
        output.ShouldContain("[warn] section 'ai'");
        output.ShouldContain("[warn] section 'policies'");
    }

    [Fact]
    public async Task ApplyAsync_HappyPath_CreatesUnitThenAddsMembersInOrder()
    {
        var handler = new RecordingHandler(
            (req, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        var http = new HttpClient(handler);
        var client = new SpringApiClient(http, "http://localhost:5000");

        var manifest = ApplyRunner.Parse(EngineeringTeamYaml);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ApplyRunner.ApplyAsync(
            manifest, client, stdout, stderr, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        stderr.ToString().ShouldBeEmpty();

        handler.Calls.Count().ShouldBe(4);
        handler.Calls[0].Method.ShouldBe(HttpMethod.Post);
        handler.Calls[0].Path.ShouldBe("/api/v1/units");
        handler.Calls[1].Path.ShouldBe("/api/v1/units/engineering-team/members");
        handler.Calls[2].Path.ShouldBe("/api/v1/units/engineering-team/members");
        handler.Calls[3].Path.ShouldBe("/api/v1/units/engineering-team/members");

        // The create-unit POST must carry the full server-side contract
        // (Name + DisplayName + Description) so CreateUnitRequest binds
        // correctly and the description from the manifest is persisted.
        using var createBody = System.Text.Json.JsonDocument.Parse(handler.Calls[0].Body);
        createBody.RootElement.GetProperty("name").GetString().ShouldBe("engineering-team");
        createBody.RootElement.GetProperty("displayName").GetString().ShouldNotBeNullOrEmpty();
        createBody.RootElement.GetProperty("description").GetString()
            .ShouldStartWith("A software engineering team");

        // Bodies record the scheme/path for each member in declaration order.
        handler.Calls[1].Body.ShouldContain("tech-lead");
        handler.Calls[2].Body.ShouldContain("backend-engineer");
        handler.Calls[3].Body.ShouldContain("qa-engineer");

        var stdoutText = stdout.ToString();
        stdoutText.ShouldContain("creating unit 'engineering-team'");
        stdoutText.ShouldContain("added member agent:tech-lead");
        stdoutText.ShouldContain("3 member(s) added");
    }

    [Fact]
    public async Task ApplyAsync_AddMemberFails_ReturnsNonZeroAndWritesToStderr()
    {
        var call = 0;
        var handler = new RecordingHandler((req, _) =>
        {
            call++;
            // First call (create unit) succeeds. Second (first add member) blows up.
            if (call == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom", System.Text.Encoding.UTF8, "text/plain"),
            };
        });
        var http = new HttpClient(handler);
        var client = new SpringApiClient(http, "http://localhost:5000");

        var manifest = ApplyRunner.Parse(EngineeringTeamYaml);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ApplyRunner.ApplyAsync(
            manifest, client, stdout, stderr, TestContext.Current.CancellationToken);

        exitCode.ShouldNotBe(0);

        // Unit creation must have been attempted and reported before failure.
        stdout.ToString().ShouldContain("creating unit 'engineering-team'");

        // Failure surfaced to stderr.
        var stderrText = stderr.ToString();
        stderrText.ShouldContain("[error]");
        stderrText.ShouldContain("tech-lead");

        // Only the create + one member call should have been attempted.
        handler.Calls.Count().ShouldBe(2);
    }

    // ---- #494: boundary manifest support -------------------------------

    private const string BoundaryUnitYaml = """
        unit:
          name: triage-cell
          description: cell with a configured boundary
          members:
            - agent: triager
          boundary:
            opacities:
              - domain_pattern: internal-*
              - origin_pattern: agent://secret-*
            projections:
              - domain_pattern: backend-*
                rename_to: engineering
                override_level: advanced
            syntheses:
              - name: full-stack
                domain_pattern: frontend
                level: expert
                description: team-level full-stack coverage
        """;

    [Fact]
    public void Parse_BoundaryBlock_MapsEveryRuleShape()
    {
        var manifest = ApplyRunner.Parse(BoundaryUnitYaml);

        manifest.Boundary.ShouldNotBeNull();
        manifest.Boundary!.IsEmpty.ShouldBeFalse();

        manifest.Boundary.Opacities.ShouldNotBeNull();
        manifest.Boundary.Opacities!.Count.ShouldBe(2);
        manifest.Boundary.Opacities![0].DomainPattern.ShouldBe("internal-*");
        manifest.Boundary.Opacities![1].OriginPattern.ShouldBe("agent://secret-*");

        manifest.Boundary.Projections.ShouldNotBeNull();
        manifest.Boundary.Projections!.Count.ShouldBe(1);
        manifest.Boundary.Projections![0].DomainPattern.ShouldBe("backend-*");
        manifest.Boundary.Projections![0].RenameTo.ShouldBe("engineering");
        manifest.Boundary.Projections![0].OverrideLevel.ShouldBe("advanced");

        manifest.Boundary.Syntheses.ShouldNotBeNull();
        manifest.Boundary.Syntheses!.Count.ShouldBe(1);
        manifest.Boundary.Syntheses![0].Name.ShouldBe("full-stack");
        manifest.Boundary.Syntheses![0].Level.ShouldBe("expert");
        manifest.Boundary.Syntheses![0].Description.ShouldBe("team-level full-stack coverage");
    }

    [Fact]
    public void Parse_NoBoundaryBlock_BoundaryIsNull()
    {
        var manifest = ApplyRunner.Parse("unit:\n  name: plain\n");
        manifest.Boundary.ShouldBeNull();
    }

    [Fact]
    public void Parse_EmptyBoundaryBlock_IsEmptyTrue()
    {
        var yaml = """
            unit:
              name: empty-boundary
              boundary:
                opacities: []
                projections: []
                syntheses: []
            """;

        var manifest = ApplyRunner.Parse(yaml);
        manifest.Boundary.ShouldNotBeNull();
        manifest.Boundary!.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task ApplyAsync_WithBoundary_PutsBoundaryAfterMembers()
    {
        var handler = new RecordingHandler(
            (req, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        var http = new HttpClient(handler);
        var client = new SpringApiClient(http, "http://localhost:5000");

        var manifest = ApplyRunner.Parse(BoundaryUnitYaml);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ApplyRunner.ApplyAsync(
            manifest, client, stdout, stderr, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        stderr.ToString().ShouldBeEmpty();

        // Expected: POST unit, POST member, PUT boundary — exactly three
        // calls in that order.
        handler.Calls.Count().ShouldBe(3);
        handler.Calls[0].Path.ShouldBe("/api/v1/units");
        handler.Calls[1].Path.ShouldBe("/api/v1/units/triage-cell/members");
        handler.Calls[2].Method.ShouldBe(HttpMethod.Put);
        handler.Calls[2].Path.ShouldBe("/api/v1/units/triage-cell/boundary");

        using var boundaryBody = System.Text.Json.JsonDocument.Parse(handler.Calls[2].Body);
        var root = boundaryBody.RootElement;
        root.GetProperty("opacities").GetArrayLength().ShouldBe(2);
        root.GetProperty("projections").GetArrayLength().ShouldBe(1);
        root.GetProperty("syntheses").GetArrayLength().ShouldBe(1);
        root.GetProperty("syntheses")[0].GetProperty("name").GetString().ShouldBe("full-stack");

        stdout.ToString().ShouldContain("applied boundary rules for unit 'triage-cell'");
    }

    [Fact]
    public async Task ApplyAsync_NoBoundaryBlock_DoesNotPutBoundary()
    {
        var handler = new RecordingHandler(
            (req, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        var http = new HttpClient(handler);
        var client = new SpringApiClient(http, "http://localhost:5000");

        var manifest = ApplyRunner.Parse(EngineeringTeamYaml);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ApplyRunner.ApplyAsync(
            manifest, client, stdout, stderr, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        handler.Calls.ShouldAllBe(c => !c.Path.Contains("/boundary"));
    }

    [Fact]
    public async Task ApplyAsync_EmptyBoundaryBlock_DoesNotPutBoundary()
    {
        // An empty boundary block is semantically equivalent to "no
        // boundary" — don't waste a round-trip replacing whatever the unit
        // already has with the same empty shape, and don't log a misleading
        // "applied boundary rules" line.
        var yaml = """
            unit:
              name: empty-b
              boundary:
                opacities: []
            """;
        var handler = new RecordingHandler(
            (req, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        var http = new HttpClient(handler);
        var client = new SpringApiClient(http, "http://localhost:5000");

        var manifest = ApplyRunner.Parse(yaml);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ApplyRunner.ApplyAsync(
            manifest, client, stdout, stderr, TestContext.Current.CancellationToken);

        exitCode.ShouldBe(0);
        handler.Calls.ShouldAllBe(c => !c.Path.Contains("/boundary"));
    }

    [Fact]
    public async Task ApplyAsync_BoundaryPutFails_ReturnsNonZeroAndWritesToStderr()
    {
        var call = 0;
        var handler = new RecordingHandler((req, _) =>
        {
            call++;
            // Create + member succeed. PUT boundary blows up.
            if (call < 3)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom", System.Text.Encoding.UTF8, "text/plain"),
            };
        });
        var http = new HttpClient(handler);
        var client = new SpringApiClient(http, "http://localhost:5000");

        var manifest = ApplyRunner.Parse(BoundaryUnitYaml);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ApplyRunner.ApplyAsync(
            manifest, client, stdout, stderr, TestContext.Current.CancellationToken);

        exitCode.ShouldNotBe(0);
        stderr.ToString().ShouldContain("failed to apply boundary for unit 'triage-cell'");
    }

    [Fact]
    public void PrintPlan_WithBoundary_LogsBoundaryStep()
    {
        var manifest = ApplyRunner.Parse(BoundaryUnitYaml);
        using var writer = new StringWriter();
        ApplyRunner.PrintPlan(manifest, writer);
        var output = writer.ToString();
        output.ShouldContain("apply boundary");
        output.ShouldContain("opacities: 2");
        output.ShouldContain("projections: 1");
        output.ShouldContain("syntheses: 1");
    }

    /// <summary>
    /// Test <see cref="HttpMessageHandler"/> that records every request it sees and
    /// delegates response construction to a caller-supplied factory.
    /// </summary>
    private sealed class RecordingHandler(
        System.Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<RecordedCall> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = string.Empty;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            }
            Calls.Add(new RecordedCall(request.Method, request.RequestUri!.AbsolutePath, body));
            return responder(request, cancellationToken);
        }
    }

    private sealed record RecordedCall(HttpMethod Method, string Path, string Body);
}