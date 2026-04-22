// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

using Cvoya.Spring.Cli.ErrorHandling;
using Cvoya.Spring.Cli.Generated.Models;

using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Serialization;

using Shouldly;

using Xunit;
// Disambiguate System.Text.Json.JsonElement from the generated
// Cvoya.Spring.Cli.Generated.Models.JsonElement that ships alongside
// the Kiota client.
using JsonElement = System.Text.Json.JsonElement;

/// <summary>
/// Locks the behaviour of the central CLI error renderer introduced for
/// #1071 (no raw stack traces on non-2xx) and #1068 (JSON mode surfaces
/// operator hints). The tests exercise the default
/// <see cref="ApiExceptionRenderer"/> directly because every command site
/// dispatches through <see cref="ApiExceptionRenderer.Instance"/>; the
/// mapping logic lives here, not at the call sites.
///
/// All output is captured through <see cref="CliRenderContext.StdOut"/> /
/// <see cref="CliRenderContext.StdErr"/> writer overrides rather than by
/// mutating the static <see cref="System.Console"/> handles. xUnit runs
/// test classes in parallel, and swapping <c>Console.Out</c> in one class
/// while another writes to it produces non-deterministic
/// <see cref="System.ObjectDisposedException"/>s.
/// </summary>
public class ApiExceptionRendererTests
{
    [Fact]
    public void Render_RawApiException_404_EmitsFriendlyProse_NotStackTrace()
    {
        // Kiota throws a bare ApiException for status codes the OpenAPI
        // contract does not map (e.g. message send to a non-existent
        // address). Pre-#1071 this surfaced the .NET stack trace; the
        // renderer now reduces it to a single line keyed by status code.
        var exception = new ApiException("server returned 404") { ResponseStatusCode = 404 };

        var (exitCode, _, stderr) = Capture((stdout, stderr) =>
            new ApiExceptionRenderer().Render(
                exception,
                new CliRenderContext("table", Verbose: false, "Failed to send message", stdout, stderr)));

        exitCode.ShouldBe(1);
        stderr.ShouldContain("Failed to send message");
        stderr.ShouldContain("[404]");
        stderr.ShouldContain("Resource not found");
        stderr.ShouldNotContain("at ");
        stderr.ShouldNotContain("Microsoft.Kiota");
    }

    [Fact]
    public void Render_VerboseMode_AppendsStackTraceToStderr()
    {
        var exception = MakeThrownException(403);

        var (exitCode, _, stderr) = Capture((stdout, stderr) =>
            new ApiExceptionRenderer().Render(
                exception,
                new CliRenderContext("table", Verbose: true, null, stdout, stderr)));

        exitCode.ShouldBe(1);
        stderr.ShouldContain("[403]");
        stderr.ShouldContain("Operation is not allowed");
        stderr.ShouldContain("Microsoft.Kiota.Abstractions.ApiException");
    }

    [Fact]
    public void Render_JsonMode_EmitsEnvelopeOnStdout_WithStatusAndTitle()
    {
        var exception = new ApiException("not found") { ResponseStatusCode = 404 };

        var (exitCode, stdout, _) = Capture((so, se) =>
            new ApiExceptionRenderer().Render(
                exception,
                new CliRenderContext("json", Verbose: false, "Failed to send message", so, se)));

        exitCode.ShouldBe(1);
        var json = JsonSerializer.Deserialize<JsonElement>(stdout);
        var error = json.GetProperty("error");
        error.GetProperty("status").GetInt32().ShouldBe(404);
        error.GetProperty("title").GetString()!.ShouldContain("Resource not found");
        error.GetProperty("operation").GetString().ShouldBe("Failed to send message");
        // Stack trace must NOT escape to stdout in non-verbose mode — that
        // would corrupt the JSON envelope for scripted consumers.
        stdout.ShouldNotContain("Microsoft.Kiota");
    }

    [Fact]
    public void Render_JsonMode_PreservesOperatorHintsFromProblemDetailsExtensions()
    {
        // #1068: the API host's `unit purge` gate emits anonymous
        // { Hint, ForceHint } in a 409 body; Kiota deserialises those
        // through ProblemDetails.AdditionalData. Both extensions must
        // survive into the JSON envelope's `error.next` block so scripts
        // can auto-recover.
        var problem = new ProblemDetails
        {
            Title = "Unit is not stopped",
            Detail = "Unit 'demo' is Running; stop it before deleting.",
            Status = new UntypedInteger(409),
            AdditionalData = new Dictionary<string, object>
            {
                ["Hint"] = "POST /api/v1/units/demo/stop",
                ["ForceHint"] = "DELETE /api/v1/units/demo?force=true bypasses the gate.",
                ["CurrentStatus"] = "Running",
            },
        };
        problem.ResponseStatusCode = 409;

        var (_, stdout, _) = Capture((so, se) =>
            new ApiExceptionRenderer().Render(
                problem,
                new CliRenderContext("json", Verbose: false, "Failed to purge unit 'demo'", so, se)));

        var json = JsonSerializer.Deserialize<JsonElement>(stdout);
        var error = json.GetProperty("error");
        error.GetProperty("status").GetInt32().ShouldBe(409);
        error.GetProperty("title").GetString().ShouldBe("Unit is not stopped");
        error.GetProperty("detail").GetString()!.ShouldContain("stop it before deleting");

        var next = error.GetProperty("next");
        next.ValueKind.ShouldBe(JsonValueKind.Array);
        var hints = new List<string>();
        foreach (var item in next.EnumerateArray())
        {
            hints.Add(item.GetString()!);
        }
        hints.ShouldContain(h => h.Contains("/stop"));
        hints.ShouldContain(h => h.Contains("force=true"));

        // Non-hint extensions (CurrentStatus) flow through the
        // `extensions` slot — they're useful for scripts but not
        // recovery hints.
        var extensions = error.GetProperty("extensions");
        extensions.GetProperty("currentStatus").GetString().ShouldBe("Running");
    }

    [Fact]
    public void Render_ProseMode_PrintsHintsUnderNextBlock()
    {
        var problem = new ProblemDetails
        {
            Title = "Unit is not stopped",
            Detail = "Unit 'demo' is Running; stop it before deleting.",
            Status = new UntypedInteger(409),
            AdditionalData = new Dictionary<string, object>
            {
                ["Hint"] = "POST /api/v1/units/demo/stop",
                ["ForceHint"] = "DELETE /api/v1/units/demo?force=true bypasses the gate.",
            },
        };
        problem.ResponseStatusCode = 409;

        var (_, _, stderr) = Capture((so, se) =>
            new ApiExceptionRenderer().Render(
                problem,
                new CliRenderContext("table", Verbose: false, "Failed to purge unit 'demo'", so, se)));

        stderr.ShouldContain("Failed to purge unit 'demo' [409]");
        stderr.ShouldContain("Unit is not stopped");
        stderr.ShouldContain("next:");
        stderr.ShouldContain("POST /api/v1/units/demo/stop");
        stderr.ShouldContain("force=true");
    }

    [Fact]
    public void Render_PreservesProblemDetailsTitleOverStatusFallback()
    {
        var problem = new ProblemDetails
        {
            Title = "Custom title from server",
            Detail = "Some detail.",
        };
        problem.ResponseStatusCode = 500;

        var (_, _, stderr) = Capture((so, se) =>
            new ApiExceptionRenderer().Render(
                problem,
                new CliRenderContext("table", Verbose: false, null, so, se)));

        stderr.ShouldContain("Custom title from server");
        stderr.ShouldNotContain("Server error.");
    }

    [Fact]
    public void Render_JsonMode_OmitsNullFieldsToKeepEnvelopeTerse()
    {
        var exception = new ApiException("err") { ResponseStatusCode = 500 };

        var (_, stdout, _) = Capture((so, se) =>
            new ApiExceptionRenderer().Render(
                exception,
                new CliRenderContext("json", Verbose: false, null, so, se)));

        var json = JsonSerializer.Deserialize<JsonElement>(stdout);
        var error = json.GetProperty("error");
        // No detail, no operation, no extensions => those keys must be
        // absent from the envelope (WhenWritingNull policy) so consumers
        // can use jq's `has()` instead of carrying nullable handling.
        error.TryGetProperty("detail", out _).ShouldBeFalse();
        error.TryGetProperty("operation", out _).ShouldBeFalse();
        error.TryGetProperty("next", out _).ShouldBeFalse();
        error.TryGetProperty("extensions", out _).ShouldBeFalse();
        error.GetProperty("status").GetInt32().ShouldBe(500);
    }

    /// <summary>
    /// Runs <paramref name="action"/> with two fresh in-memory
    /// <see cref="StringWriter"/>s and returns the captured streams.
    /// The writers are local — the test never touches
    /// <c>Console.Out</c>/<c>Console.Error</c>, so parallel xUnit
    /// classes can run alongside without ObjectDisposedException churn.
    /// </summary>
    private static (int ExitCode, string Stdout, string Stderr) Capture(
        Func<TextWriter, TextWriter, int> action)
    {
        using var so = new StringWriter();
        using var se = new StringWriter();
        var exit = action(so, se);
        return (exit, so.ToString(), se.ToString());
    }

    private static ApiException MakeThrownException(int status)
    {
        // Building a "thrown" ApiException so the renderer's verbose path
        // has a stack trace to inspect — the constructor doesn't capture
        // one until throw fills it in.
        try
        {
            throw new ApiException("simulated") { ResponseStatusCode = status };
        }
        catch (ApiException ex)
        {
            return ex;
        }
    }
}