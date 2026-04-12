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

using FluentAssertions;

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

        manifest.Name.Should().Be("engineering-team");
        manifest.Description.Should().StartWith("A software engineering team");
        manifest.Structure.Should().Be("hierarchical");
        manifest.Members.Should().NotBeNull().And.HaveCount(3);
        manifest.Members![0].Agent.Should().Be("tech-lead");
        manifest.Members[1].Agent.Should().Be("backend-engineer");
        manifest.Members[2].Agent.Should().Be("qa-engineer");

        manifest.Ai.Should().NotBeNull();
        manifest.Ai!.Agent.Should().Be("claude");
        manifest.Ai.Model.Should().Be("claude-sonnet-4-20250514");
        manifest.Ai.Skills.Should().NotBeNull().And.HaveCount(1);

        manifest.Execution.Should().NotBeNull();
        manifest.Execution!.Image.Should().Be("spring-agent:latest");
        manifest.Connectors.Should().NotBeNull().And.HaveCount(1);
        manifest.Policies.Should().NotBeNull().And.ContainKey("communication");
        manifest.Humans.Should().NotBeNull().And.HaveCount(1);
    }

    [Fact]
    public void Parse_MinimalManifest_AllOtherSectionsNull()
    {
        var yaml = "unit:\n  name: x\n";

        var manifest = ApplyRunner.Parse(yaml);

        manifest.Name.Should().Be("x");
        manifest.Description.Should().BeNull();
        manifest.Ai.Should().BeNull();
        manifest.Members.Should().BeNull();
        manifest.Connectors.Should().BeNull();
        manifest.Policies.Should().BeNull();
        manifest.Humans.Should().BeNull();
        manifest.Execution.Should().BeNull();
    }

    [Fact]
    public void Parse_MissingUnitName_Throws()
    {
        var yaml = "unit:\n  description: no name here\n";

        var act = () => ApplyRunner.Parse(yaml);

        act.Should().Throw<ManifestParseException>()
            .WithMessage("*unit.name*");
    }

    [Fact]
    public void Parse_MissingUnitRoot_Throws()
    {
        var yaml = "other: 1\n";

        var act = () => ApplyRunner.Parse(yaml);

        act.Should().Throw<ManifestParseException>()
            .WithMessage("*'unit' root section*");
    }

    [Fact]
    public void PrintPlan_DryRun_MentionsUnitAndEachMember()
    {
        var manifest = ApplyRunner.Parse(EngineeringTeamYaml);

        using var writer = new StringWriter();
        ApplyRunner.PrintPlan(manifest, writer);

        var output = writer.ToString();

        output.Should().Contain("engineering-team");
        output.Should().Contain("agent:tech-lead");
        output.Should().Contain("agent:backend-engineer");
        output.Should().Contain("agent:qa-engineer");
        output.Should().Contain("no API calls were made");
        output.Should().Contain("[warn] section 'ai'");
        output.Should().Contain("[warn] section 'policies'");
    }

    [Fact]
    public async Task ApplyAsync_HappyPath_CreatesUnitThenAddsMembersInOrder()
    {
        var handler = new RecordingHandler(
            (req, _) => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
            });
        var http = new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:5000") };
        var client = new SpringApiClient(http);

        var manifest = ApplyRunner.Parse(EngineeringTeamYaml);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ApplyRunner.ApplyAsync(
            manifest, client, stdout, stderr, TestContext.Current.CancellationToken);

        exitCode.Should().Be(0);
        stderr.ToString().Should().BeEmpty();

        handler.Calls.Should().HaveCount(4);
        handler.Calls[0].Method.Should().Be(HttpMethod.Post);
        handler.Calls[0].Path.Should().Be("/api/v1/units");
        handler.Calls[1].Path.Should().Be("/api/v1/units/engineering-team/members");
        handler.Calls[2].Path.Should().Be("/api/v1/units/engineering-team/members");
        handler.Calls[3].Path.Should().Be("/api/v1/units/engineering-team/members");

        // Bodies record the scheme/path for each member in declaration order.
        handler.Calls[1].Body.Should().Contain("tech-lead");
        handler.Calls[2].Body.Should().Contain("backend-engineer");
        handler.Calls[3].Body.Should().Contain("qa-engineer");

        var stdoutText = stdout.ToString();
        stdoutText.Should().Contain("creating unit 'engineering-team'");
        stdoutText.Should().Contain("added member agent:tech-lead");
        stdoutText.Should().Contain("3 member(s) added");
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
        var http = new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:5000") };
        var client = new SpringApiClient(http);

        var manifest = ApplyRunner.Parse(EngineeringTeamYaml);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await ApplyRunner.ApplyAsync(
            manifest, client, stdout, stderr, TestContext.Current.CancellationToken);

        exitCode.Should().NotBe(0);

        // Unit creation must have been attempted and reported before failure.
        stdout.ToString().Should().Contain("creating unit 'engineering-team'");

        // Failure surfaced to stderr.
        var stderrText = stderr.ToString();
        stderrText.Should().Contain("[error]");
        stderrText.Should().Contain("tech-lead");

        // Only the create + one member call should have been attempted.
        handler.Calls.Should().HaveCount(2);
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