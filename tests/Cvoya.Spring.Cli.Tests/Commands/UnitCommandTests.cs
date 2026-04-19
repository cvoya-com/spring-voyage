// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using Cvoya.Spring.Cli.Commands;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the free-function validators on
/// <see cref="UnitCommand"/>. The action pipelines themselves are
/// exercised end-to-end elsewhere — these tests pin the validation
/// contract so <c>spring unit create</c> and
/// <c>spring unit create-from-template</c> reject mis-composed flag
/// sets consistently (#598).
/// </summary>
public class UnitCommandTests
{
    // Canonical rejection message — operators read this verbatim when
    // they combine --provider / --model with a non-dapr-agent tool.
    private const string ExpectedErrorMessage =
        "--provider and --model are only meaningful for --tool=dapr-agent; " +
        "other tools (claude-code, codex, gemini) have their provider hardcoded in the tool CLI.";

    [Theory]
    [InlineData("dapr-agent", "openai", "gpt-4o")]
    [InlineData("dapr-agent", "anthropic", null)]
    [InlineData("dapr-agent", null, "claude-sonnet-4-20250514")]
    [InlineData("dapr-agent", null, null)]
    public void ValidateProviderModelAgainstTool_DaprAgent_Accepts(
        string tool,
        string? provider,
        string? model)
    {
        UnitCommand.ValidateProviderModelAgainstTool(tool, provider, model)
            .ShouldBeNull();
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(null, null, "")]
    // No --tool supplied and no provider/model either — nothing to reject.
    [InlineData("", null, null)]
    public void ValidateProviderModelAgainstTool_NoFlags_Accepts(
        string? tool,
        string? provider,
        string? model)
    {
        UnitCommand.ValidateProviderModelAgainstTool(tool, provider, model)
            .ShouldBeNull();
    }

    [Theory]
    [InlineData("claude-code", "anthropic", null)]
    [InlineData("claude-code", null, "claude-sonnet-4-20250514")]
    [InlineData("claude-code", "anthropic", "claude-sonnet-4-20250514")]
    [InlineData("codex", "openai", "gpt-4o")]
    [InlineData("codex", "openai", null)]
    [InlineData("gemini", "google", "gemini-2.5-pro")]
    [InlineData("gemini", null, "gemini-2.5-pro")]
    [InlineData("custom", "ollama", "llama3.2:3b")]
    public void ValidateProviderModelAgainstTool_NonDaprAgent_RejectsWithCanonicalMessage(
        string tool,
        string? provider,
        string? model)
    {
        var error = UnitCommand.ValidateProviderModelAgainstTool(tool, provider, model);
        error.ShouldBe(ExpectedErrorMessage);
    }

    [Fact]
    public void ValidateProviderModelAgainstTool_CaseInsensitive_OnTool()
    {
        // The option's allow-list is lowercase but operators sometimes
        // type "Dapr-Agent"; the validator must normalise before the
        // check so they're not rejected for a casing accident.
        UnitCommand.ValidateProviderModelAgainstTool(
            "Dapr-Agent",
            provider: "openai",
            model: "gpt-4o")
            .ShouldBeNull();
    }

    [Fact]
    public void ValidateProviderModelAgainstTool_NoToolProvided_DoesNotSecondGuessServerDefault()
    {
        // When --tool is omitted the server picks the deployment default
        // (claude-code in today's build). The CLI must not assume that
        // default — rejecting --provider in that case would mean
        // operators who pin provider + model without passing --tool hit
        // a confusing error. The server already enforces the honest
        // contract at dispatch time.
        UnitCommand.ValidateProviderModelAgainstTool(
            tool: null,
            provider: "openai",
            model: "gpt-4o")
            .ShouldBeNull();
    }
}