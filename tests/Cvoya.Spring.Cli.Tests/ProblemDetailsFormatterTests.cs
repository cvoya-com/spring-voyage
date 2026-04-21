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
/// Pins the human-readable shape of <see cref="ProblemDetailsFormatter"/>.
/// The formatter closes #982: before it, the CLI's catch-all printed the
/// Kiota <see cref="ProblemDetails"/>'s default <c>Exception.Message</c>
/// ("Exception of type '…' was thrown.") instead of the server's title
/// and detail. These tests lock the four shapes the issue calls out.
/// </summary>
public class ProblemDetailsFormatterTests
{
    [Fact]
    public void Format_TitleAndDetailOnly_RendersEmDashSeparator()
    {
        var problem = new ProblemDetails
        {
            Title = "Invalid state",
            Detail = "Unit 'portal-scratch-1' is Draft; revalidation is only allowed from Error or Stopped.",
        };

        var rendered = ProblemDetailsFormatter.Format(problem);

        rendered.ShouldBe(
            "Invalid state — Unit 'portal-scratch-1' is Draft; revalidation is only allowed from Error or Stopped.");
    }

    [Fact]
    public void Format_WithCodeAndCurrentStatusExtensions_EmitsIndentedLines()
    {
        // Sorted alphabetically so the output is deterministic regardless of
        // dictionary iteration order — operators reading the stderr stream
        // should see the same shape on every run.
        var problem = new ProblemDetails
        {
            Title = "Invalid state",
            Detail = "Unit 'portal-scratch-1' is Draft; revalidation is only allowed from Error or Stopped.",
            AdditionalData = new Dictionary<string, object>
            {
                ["code"] = "InvalidState",
                ["currentStatus"] = "Draft",
            },
        };

        var rendered = ProblemDetailsFormatter.Format(problem);

        rendered.ShouldBe(
            "Invalid state — Unit 'portal-scratch-1' is Draft; revalidation is only allowed from Error or Stopped."
            + "\n  code: InvalidState"
            + "\n  currentStatus: Draft");
    }

    [Fact]
    public void Format_Minimal_NoDetail_EmitsJustTheTitle()
    {
        var problem = new ProblemDetails
        {
            Title = "Invalid state",
        };

        var rendered = ProblemDetailsFormatter.Format(problem);

        rendered.ShouldBe("Invalid state");
    }

    [Fact]
    public void Format_EmptyAdditionalData_OmitsExtensionBlock()
    {
        var problem = new ProblemDetails
        {
            Title = "Invalid state",
            Detail = "Already revalidating.",
            AdditionalData = new Dictionary<string, object>(),
        };

        var rendered = ProblemDetailsFormatter.Format(problem);

        rendered.ShouldBe("Invalid state — Already revalidating.");
    }

    [Fact]
    public void Format_NullTitle_WithStatusUntypedInteger_FallsBackToStatusLine()
    {
        // The generated ProblemDetails types Status as UntypedNode?. When the
        // server omits title but includes status (RFC 7807 allows title to
        // default to the HTTP reason phrase), we emit "Status {value}" so
        // the message isn't blank.
        var problem = new ProblemDetails
        {
            Status = new UntypedInteger(409),
            Detail = "Unit 'x' is Draft; revalidation is only allowed from Error or Stopped.",
        };

        var rendered = ProblemDetailsFormatter.Format(problem);

        rendered.ShouldBe(
            "Status 409 — Unit 'x' is Draft; revalidation is only allowed from Error or Stopped.");
    }

    [Fact]
    public void Format_TraceIdExtension_IsEmitted()
    {
        var problem = new ProblemDetails
        {
            Title = "Internal error",
            Detail = "The server hit an unexpected condition.",
            AdditionalData = new Dictionary<string, object>
            {
                ["traceId"] = "00-abc123-xyz-00",
            },
        };

        var rendered = ProblemDetailsFormatter.Format(problem);

        rendered.ShouldContain("\n  traceId: 00-abc123-xyz-00");
    }

    [Fact]
    public void Format_NonScalarExtension_IsSkipped()
    {
        // Nested objects/arrays aren't useful as a single indented line; the
        // formatter deliberately drops them so operators aren't shown
        // opaque Kiota UntypedObject.ToString() payloads.
        var nested = new UntypedObject(new Dictionary<string, UntypedNode>
        {
            ["inner"] = new UntypedString("value"),
        });
        var problem = new ProblemDetails
        {
            Title = "Invalid state",
            Detail = "Unit mismatch.",
            AdditionalData = new Dictionary<string, object>
            {
                ["code"] = "InvalidState",
                ["nested"] = nested,
            },
        };

        var rendered = ProblemDetailsFormatter.Format(problem);

        rendered.ShouldBe(
            "Invalid state — Unit mismatch."
            + "\n  code: InvalidState");
    }

    [Fact]
    public void Format_UntypedStringScalar_Unwraps()
    {
        var problem = new ProblemDetails
        {
            Title = "Invalid state",
            Detail = "Unit mismatch.",
            AdditionalData = new Dictionary<string, object>
            {
                ["code"] = new UntypedString("InvalidState"),
            },
        };

        var rendered = ProblemDetailsFormatter.Format(problem);

        rendered.ShouldBe(
            "Invalid state — Unit mismatch."
            + "\n  code: InvalidState");
    }

    [Fact]
    public void Format_ExceptionOverload_RoutesProblemDetailsThroughFormatter()
    {
        // The catch-site convenience overload accepts any Exception; when it
        // is actually a ProblemDetails (Kiota subclasses ApiException as
        // ProblemDetails for the /problem+json error body), we render the
        // structured fields instead of the useless default Message.
        var problem = new ProblemDetails
        {
            Title = "Invalid state",
            Detail = "Unit mismatch.",
        };

        var rendered = ProblemDetailsFormatter.Format((Exception)problem);

        rendered.ShouldBe("Invalid state — Unit mismatch.");
    }

    [Fact]
    public void Format_ExceptionOverload_NonProblemDetails_FallsBackToMessage()
    {
        var ex = new InvalidOperationException("boom");

        ProblemDetailsFormatter.Format(ex).ShouldBe("boom");
    }
}