// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using System.CommandLine;
using System.Net;
using System.Net.Http;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the <c>spring human</c> verb tree (ADR-0045 §7). The
/// command's <c>set</c> verb mirrors <c>spring agent set</c> / <c>spring
/// unit set</c> — accepting <c>--display-name</c> and <c>--description</c>
/// — and routes the wire PATCH through the Kiota-generated
/// <see cref="SpringApiClient.UpdateHumanAsync"/> entry point.
/// </summary>
public class HumanCommandTests
{
    private const string BaseUrl = "http://localhost:5000";

    private static Option<string> CreateOutputOption()
        => new("--output", "-o")
        {
            Description = "Output format",
            DefaultValueFactory = _ => "table",
            Recursive = true,
        };

    // ── Phase 5 gap #4: --display-name + --description PATCHes both ──────

    [Fact]
    public async Task UpdateHumanAsync_BothFieldsSupplied_SendsPatchWithBothInBody()
    {
        // ADR-0045 §7: `spring human set --display-name "Foo"
        // --description "Bar"` must PATCH both fields. The CLI never opens
        // raw HTTP — every write goes through the Kiota-generated
        // SpringApiClient. Assert the wire shape here so a future refactor
        // that accidentally drops one of the slots on the PATCH body is
        // caught at the boundary.
        var humanId = Guid.Parse("00000000-aaaa-aaaa-aaaa-000000000001");
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(
            expectedPath: $"/api/v1/tenant/humans/{humanId:D}",
            expectedMethod: HttpMethod.Patch,
            responseBody:
                $$"""{"id":"{{humanId}}","displayName":"Foo","description":"Bar"}""",
            validateRequestBody: body => capturedBody = body);
        var httpClient = new HttpClient(handler);
        var client = new SpringApiClient(httpClient, BaseUrl);

        var result = await client.UpdateHumanAsync(
            humanId,
            displayName: "Foo",
            description: "Bar",
            ct: TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
        capturedBody.ShouldNotBeNull();
        capturedBody!.ShouldContain("\"displayName\"");
        capturedBody.ShouldContain("Foo");
        capturedBody.ShouldContain("\"description\"");
        capturedBody.ShouldContain("Bar");
        result.ShouldNotBeNull();
    }

    [Fact]
    public async Task UpdateHumanAsync_DisplayNameOnly_DescriptionStaysAbsent()
    {
        // The HumanCommand action distinguishes "flag absent" (null —
        // leave unchanged) from "flag passed empty" (empty string —
        // clear). When the operator supplies only --display-name, the
        // wire body must NOT carry a string value for description; Kiota
        // drops null-valued properties from the serialised body entirely,
        // so the backend's PATCH handler receives no description key and
        // takes the "no change" branch.
        var humanId = Guid.Parse("00000000-aaaa-aaaa-aaaa-000000000001");
        string? capturedBody = null;
        var handler = new MockHttpMessageHandler(
            expectedPath: $"/api/v1/tenant/humans/{humanId:D}",
            expectedMethod: HttpMethod.Patch,
            responseBody:
                $$"""{"id":"{{humanId}}","displayName":"Foo","description":null}""",
            validateRequestBody: body => capturedBody = body);
        var client = new SpringApiClient(new HttpClient(handler), BaseUrl);

        await client.UpdateHumanAsync(
            humanId,
            displayName: "Foo",
            description: null,
            ct: TestContext.Current.CancellationToken);

        handler.WasCalled.ShouldBeTrue();
        capturedBody.ShouldNotBeNull();
        capturedBody!.ShouldContain("\"displayName\"");
        capturedBody.ShouldContain("Foo");
        // Null slots round-trip out of the body. The exact wire signal is
        // "no description key" which the backend's PATCH handler keys off
        // for the leave-unchanged branch.
        capturedBody.ShouldNotContain("\"description\"");
    }

    // ── Phase 5 gap #5: missing-flag error UX (parse-surface coverage) ───

    [Fact]
    public void HumanSet_ParseSucceeds_NoFlags()
    {
        // The "both flags absent" branch is rejected at action time (not
        // parse time) so the operator sees a custom error message naming
        // both flags. The parser itself accepts the verb — the test asserts
        // this stays the case so a future regression that flips the option
        // to Required = true does not break the action layer's bespoke
        // diagnostic.
        var outputOption = CreateOutputOption();
        var humanCommand = HumanCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(humanCommand);

        var parseResult = rootCommand.Parse("human set");

        parseResult.Errors.ShouldBeEmpty();
        // Both option results should be absent — the action's
        // displayNameSupplied / descriptionSupplied checks key off this
        // exact shape to emit the "Nothing to set" diagnostic.
        parseResult.GetValue<string?>("--display-name").ShouldBeNull();
        parseResult.GetValue<string?>("--description").ShouldBeNull();
    }

    [Fact]
    public void HumanSet_ParsesBothFlagsAndId()
    {
        // The verb takes --id, --display-name, --description plus the
        // inherited --output. All three optional flags parse to their
        // string values so the action layer can call UpdateHumanAsync
        // with the right tri-state (null / value / empty).
        var outputOption = CreateOutputOption();
        var humanCommand = HumanCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(humanCommand);

        var parseResult = rootCommand.Parse(
            "human set --id aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa --display-name \"Foo\" --description \"Bar\"");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string?>("--display-name").ShouldBe("Foo");
        parseResult.GetValue<string?>("--description").ShouldBe("Bar");
        parseResult.GetValue<string?>("--id")
            .ShouldBe("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    }

    [Fact]
    public void HumanSet_DescriptionExplicitEmpty_ParsesAsEmptyString()
    {
        // `--description ""` is the clear-the-description signal —
        // distinct from omitting the flag (null = leave unchanged). The
        // parser must surface the empty string verbatim so the action's
        // descriptionSupplied check fires the clear-semantic branch.
        var outputOption = CreateOutputOption();
        var humanCommand = HumanCommand.Create(outputOption);
        var rootCommand = new RootCommand { Options = { outputOption } };
        rootCommand.Subcommands.Add(humanCommand);

        var parseResult = rootCommand.Parse("human set --description \"\"");

        parseResult.Errors.ShouldBeEmpty();
        parseResult.GetValue<string?>("--description").ShouldBe(string.Empty);
    }
}
