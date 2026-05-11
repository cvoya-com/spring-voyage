// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System;
using System.Collections.Generic;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Utilities;

using Microsoft.Kiota.Abstractions.Serialization;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the user-facing ProblemDetails translations rendered by command
/// catch-sites via <see cref="ProblemDetailsTranslator"/>.
/// </summary>
public class ProblemDetailsTranslatorTests
{
    [Theory]
    [MemberData(nameof(KnownCodeCases))]
    public void Format_KnownCodes_RendersFriendlyOneLiner(
        ProblemDetails problem,
        string expectedTitle,
        string expectedNextStep)
    {
        var rendered = ProblemDetailsTranslator.Format(problem);

        rendered.ShouldContain(expectedTitle);
        rendered.ShouldContain(expectedNextStep);
        rendered.ShouldNotContain("API error");
        rendered.ShouldNotContain("{");
        rendered.ShouldNotContain("traceId");
    }

    public static IEnumerable<object[]> KnownCodeCases()
    {
        yield return new object[]
        {
            new ProblemDetails
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "ConnectorBindingMissing",
                    ["missing"] = new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["slug"] = "github",
                            ["scope"] = "package",
                            ["unitName"] = null,
                        },
                    },
                    ["traceId"] = "00-abc",
                },
            },
            "This package needs a github connector binding.",
            "Open the github step in the wizard and pick (or set up) a connector for the package.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                Detail = "Package `spring-voyage/missing` was not found.",
                AdditionalData = new Dictionary<string, object> { ["code"] = "PackageNotFound" },
            },
            "Couldn't find package `spring-voyage/missing`.",
            "Run `spring package list` (or refresh the catalog) to confirm the package name and version.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                AdditionalData = new Dictionary<string, object> { ["code"] = "UnitNotFound" },
            },
            "Unit not found.",
            "It may have been deleted. Refresh the page or pick another unit.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                AdditionalData = new Dictionary<string, object> { ["code"] = "AgentNotFound" },
            },
            "Agent not found.",
            "It may have been deleted. Refresh the page or pick another agent.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "LifecycleConflict",
                    ["action"] = "delete",
                    ["currentStatus"] = "Running",
                    ["hint"] = "Stop the unit before deleting it.",
                },
            },
            "Can't delete this unit while it's `Running`.",
            "Stop the unit before deleting it.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "CredentialMissing",
                    ["credentialEnvVar"] = "OPENAI_API_KEY",
                },
            },
            "Required credential `OPENAI_API_KEY` isn't set.",
            "Set it in Config -> Secrets on this unit, on a parent unit, or on the tenant.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "CredentialInvalid",
                    ["provider"] = "openai",
                },
            },
            "The configured credential for `openai` was rejected by the provider.",
            "Check the secret value and try again.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                Detail = "Display name is required.",
                AdditionalData = new Dictionary<string, object> { ["code"] = "ValidationFailed" },
            },
            "The request was invalid.",
            "Display name is required.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                Detail = "manifest unknown for image ghcr.io/cvoya-com/agent-claude.",
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "ImagePullFailed",
                    ["image"] = "ghcr.io/cvoya-com/agent-claude:latest",
                },
            },
            "Couldn't pull the agent image.",
            "manifest unknown for image ghcr.io/cvoya-com/agent-claude.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "ImagePullFailed",
                },
            },
            "Couldn't pull the agent image.",
            "Check that the image exists and the host can reach the registry.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                Detail = "Container exited immediately with status 125.",
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "ImageStartFailed",
                },
            },
            "Couldn't start the agent container.",
            "Container exited immediately with status 125.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "ImageStartFailed",
                },
            },
            "Couldn't start the agent container.",
            "Check the agent image and host runtime logs.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "ToolMissing",
                    ["tool"] = "claude",
                },
            },
            "The agent image is missing the claude CLI.",
            "Pick a different agent image or install the CLI before retrying.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "ToolMissing",
                },
            },
            "The agent image is missing the required CLI.",
            "Pick a different agent image or install the CLI before retrying.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                Detail = "Token starts with sk-ant-oat (OAuth); REST requires sk-ant-api.",
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "CredentialFormatRejected",
                    ["provider"] = "anthropic",
                },
            },
            "The configured credential's format isn't accepted by anthropic.",
            "Token starts with sk-ant-oat (OAuth); REST requires sk-ant-api.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "CredentialFormatRejected",
                    ["provider"] = "anthropic",
                },
            },
            "The configured credential's format isn't accepted by anthropic.",
            "Update the secret to a value of the right shape (see the provider's docs).",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "ModelNotFound",
                    ["model"] = "claude-9-opus",
                    ["provider"] = "anthropic",
                },
            },
            "Model `claude-9-opus` isn't available for anthropic.",
            "Pick a model from the provider's catalogue or update the install.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "ProbeTimeout",
                },
            },
            "The runtime probe timed out.",
            "Verify the agent host is responsive and retry; raise the probe timeout if this is expected.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                Detail = "Interpreter threw NullReferenceException.",
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "ProbeInternalError",
                },
            },
            "The runtime probe failed unexpectedly.",
            "Interpreter threw NullReferenceException.",
        };
        yield return new object[]
        {
            new ProblemDetails
            {
                AdditionalData = new Dictionary<string, object>
                {
                    ["code"] = "ProbeInternalError",
                },
            },
            "The runtime probe failed unexpectedly.",
            "Check the host logs (`spring agent logs <id>` or `kubectl logs`) and retry.",
        };
    }

    [Fact]
    public void Format_InvalidState_UsesLifecycleTranslation()
    {
        var problem = new ProblemDetails
        {
            Detail = "Unit 'portal-scratch-1' is Draft; revalidation is only allowed from Error or Stopped.",
            AdditionalData = new Dictionary<string, object>
            {
                ["code"] = new UntypedString("InvalidState"),
                ["currentStatus"] = new UntypedString("Draft"),
            },
        };

        var rendered = ProblemDetailsTranslator.Format(problem);

        rendered.ShouldBe(
            "Can't revalidate this unit while it's `Draft`. "
            + "Wait for the current operation to finish, then retry.");
    }

    [Theory]
    [InlineData(
        "ConfigurationIncomplete",
        "This package is missing required configuration.",
        "Complete the missing configuration, then retry the install.")]
    [InlineData(
        "UnknownConnectorSlug",
        "This package doesn't declare a github connector binding.",
        "Remove that connector binding or choose a connector required by this package.")]
    [InlineData(
        "MultiParentInheritanceConflict",
        "Parent units disagree on inherited execution settings.",
        "Remove a conflicting parent or set the inherited field explicitly.")]
    public void Format_AdditionalInstallAndCreateCodes_RenderTranslations(
        string code,
        string expectedTitle,
        string expectedNextStep)
    {
        var problem = new ProblemDetails
        {
            AdditionalData = new Dictionary<string, object>
            {
                ["code"] = code,
                ["slug"] = "github",
            },
        };

        var rendered = ProblemDetailsTranslator.Format(problem);

        rendered.ShouldContain(expectedTitle);
        rendered.ShouldContain(expectedNextStep);
    }

    [Fact]
    public void Format_UnknownCode_UsesServerTitleAndDetail()
    {
        var problem = new ProblemDetails
        {
            Title = "Server supplied title",
            Detail = "Server supplied detail.",
            AdditionalData = new Dictionary<string, object>
            {
                ["code"] = "FutureCode",
                ["traceId"] = "00-abc",
            },
        };

        var rendered = ProblemDetailsTranslator.Format(problem);

        rendered.ShouldBe("Server supplied title Server supplied detail.");
        rendered.ShouldNotContain("traceId");
    }

    [Fact]
    public void Format_ExceptionOverload_RoutesProblemDetailsThroughTranslator()
    {
        var problem = new ProblemDetails
        {
            AdditionalData = new Dictionary<string, object> { ["code"] = "AgentNotFound" },
        };

        var rendered = ProblemDetailsTranslator.Format((Exception)problem);

        rendered.ShouldBe(
            "Agent not found. It may have been deleted. Refresh the page or pick another agent.");
    }

    [Fact]
    public void Format_ExceptionOverload_NonProblemDetails_FallsBackToMessage()
    {
        var ex = new InvalidOperationException("boom");

        ProblemDetailsTranslator.Format(ex).ShouldBe("boom");
    }
}
