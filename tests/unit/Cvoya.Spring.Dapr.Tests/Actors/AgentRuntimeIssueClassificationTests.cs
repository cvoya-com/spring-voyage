// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System;

using Cvoya.Spring.Core;
using Cvoya.Spring.Dapr.Actors;

using Shouldly;

using Xunit;

/// <summary>
/// #2189: pins <see cref="AgentActor.ClassifyAgentRuntimeException"/>'s
/// resolution rules — producer-stamped tags win, prefix heuristic
/// stays as the legacy fallback, free-text falls through to the
/// generic <c>AgentRuntimeError</c>.
/// </summary>
public class AgentRuntimeIssueClassificationTests
{
    [Fact]
    public void ProducerStampedTags_Win_OverEverything()
    {
        var ex = new SpringException("Some unrelated free-text message.")
            .WithIssue(code: "CredentialFormatRejected", source: "credential");

        var classification = AgentActor.ClassifyAgentRuntimeException(ex);

        classification.Source.ShouldBe("credential");
        classification.Code.ShouldBe("CredentialFormatRejected");
        classification.Title.ShouldBe("Some unrelated free-text message.");
    }

    [Fact]
    public void ProducerStampedTags_StripCodePrefixFromTitle_WhenPresent()
    {
        var ex = new SpringException(
            "CredentialFormatRejected: the configured credential is not in OAuth shape.")
            .WithIssue(code: "CredentialFormatRejected", source: "credential");

        var classification = AgentActor.ClassifyAgentRuntimeException(ex);

        classification.Title.ShouldBe("the configured credential is not in OAuth shape.");
    }

    [Fact]
    public void HeuristicPrefix_FallsBack_To_RuntimeSource_When_NoTagsPresent()
    {
        var ex = new SpringException(
            "CredentialFormatRejected: the configured credential is not in OAuth shape.");

        var classification = AgentActor.ClassifyAgentRuntimeException(ex);

        // Untagged exceptions always tag source="runtime"; the precise
        // bucket attribution requires a producer-side WithIssue stamp.
        classification.Source.ShouldBe("runtime");
        classification.Code.ShouldBe("CredentialFormatRejected");
        classification.Title.ShouldBe("the configured credential is not in OAuth shape.");
    }

    [Fact]
    public void FreeText_Untagged_Exception_FallsBack_To_AgentRuntimeError()
    {
        var ex = new InvalidOperationException("Container exited unexpectedly.");

        var classification = AgentActor.ClassifyAgentRuntimeException(ex);

        classification.Source.ShouldBe("runtime");
        classification.Code.ShouldBe("AgentRuntimeError");
        classification.Title.ShouldBe("Container exited unexpectedly.");
    }

    [Fact]
    public void TaggedException_Falls_Through_When_Only_OneKeyPresent()
    {
        // Half-tagged exceptions are treated as untagged — a producer
        // that stamps just code without source (or vice versa) would
        // otherwise mix a precise field with a heuristic one.
        var ex = new SpringException("CredentialFormatRejected: oauth shape.");
        ex.Data[SpringException.IssueCodeDataKey] = "CredentialFormatRejected";
        // IssueSourceDataKey deliberately not set.

        var classification = AgentActor.ClassifyAgentRuntimeException(ex);

        classification.Source.ShouldBe("runtime"); // heuristic fallback
    }

    [Fact]
    public void ProducerStampedTags_Win_For_Configuration_Source()
    {
        var ex = new SpringException(
            "Spring Voyage launcher cannot map provider 'unknown'.")
            .WithIssue(code: "ConfigurationIncomplete", source: "configuration");

        var classification = AgentActor.ClassifyAgentRuntimeException(ex);

        classification.Source.ShouldBe("configuration");
        classification.Code.ShouldBe("ConfigurationIncomplete");
    }
}
