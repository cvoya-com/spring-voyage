// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.Commands;

using Cvoya.Spring.Cli.Commands;
using Cvoya.Spring.Cli.Generated.Models;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for free-function helpers on <see cref="ThreadCommand"/>.
///
/// <see cref="ThreadCommand.RenderThreadEvents"/> is the primary focus:
/// it must emit <c>ErrorOccurred</c> and severity-<c>Error</c> events to
/// stderr with a leading <c>!!</c> prefix so operators see dispatch
/// failures inline, rather than having to open the activity log (#1161).
/// </summary>
public class ThreadCommandTests
{
    // ---------------------------------------------------------------------------
    // Helper — creates a minimal Kiota-generated ThreadEvent for tests.
    // The Kiota type is a class with settable properties, not a record.
    // ---------------------------------------------------------------------------

    private static ThreadEvent MakeEvent(
        string eventType,
        string severity,
        string summary,
        string? from = null,
        string? to = null,
        string? body = null)
    {
        return new ThreadEvent
        {
            Id = Guid.NewGuid(),
            Timestamp = DateTimeOffset.UtcNow,
            Source = "agent://ada",
            EventType = eventType,
            Severity = severity,
            Summary = summary,
            From = from,
            To = to,
            Body = body,
        };
    }

    // ---------------------------------------------------------------------------
    // RenderThreadEvents — #1161 error surfacing
    // ---------------------------------------------------------------------------

    [Fact]
    public void RenderThreadEvents_ErrorOccurred_WritesToStderr()
    {
        var events = new List<ThreadEvent>
        {
            MakeEvent(
                eventType: "ErrorOccurred",
                severity: "Error",
                summary: "Dispatch failed: agent did not become ready within 60s"),
        };

        var stderr = new StringWriter();
        var stdout = new StringWriter();
        var oldErr = Console.Error;
        var oldOut = Console.Out;
        Console.SetError(stderr);
        Console.SetOut(stdout);
        try
        {
            ThreadCommand.RenderThreadEvents(events);
        }
        finally
        {
            Console.SetError(oldErr);
            Console.SetOut(oldOut);
        }

        stderr.ToString().ShouldContain("!!");
        stderr.ToString().ShouldContain("Dispatch failed: agent did not become ready within 60s");
        // Must NOT appear on stdout — scripted consumers rely on the separation.
        stdout.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void RenderThreadEvents_SeverityError_WritesToStderr()
    {
        // A non-ErrorOccurred event with severity=Error (e.g. a StateChanged
        // that escalated) should also be surfaced inline via stderr.
        var events = new List<ThreadEvent>
        {
            MakeEvent(
                eventType: "StateChanged",
                severity: "Error",
                summary: "Actor state transition failed"),
        };

        var stderr = new StringWriter();
        var stdout = new StringWriter();
        var oldErr = Console.Error;
        var oldOut = Console.Out;
        Console.SetError(stderr);
        Console.SetOut(stdout);
        try
        {
            ThreadCommand.RenderThreadEvents(events);
        }
        finally
        {
            Console.SetError(oldErr);
            Console.SetOut(oldOut);
        }

        stderr.ToString().ShouldContain("!!");
        stderr.ToString().ShouldContain("Actor state transition failed");
        stdout.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void RenderThreadEvents_NormalEvents_WriteToStdout()
    {
        var events = new List<ThreadEvent>
        {
            MakeEvent(
                eventType: "MessageReceived",
                severity: "Info",
                summary: "Received message",
                from: "human://savasp",
                to: "agent://ada",
                body: "Hello!"),
        };

        var stderr = new StringWriter();
        var stdout = new StringWriter();
        var oldErr = Console.Error;
        var oldOut = Console.Out;
        Console.SetError(stderr);
        Console.SetOut(stdout);
        try
        {
            ThreadCommand.RenderThreadEvents(events);
        }
        finally
        {
            Console.SetError(oldErr);
            Console.SetOut(oldOut);
        }

        // Normal output lands on stdout; nothing on stderr.
        stdout.ToString().ShouldContain("Hello!");
        stderr.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void RenderThreadEvents_EmptyList_WritesPlaceholder()
    {
        var stdout = new StringWriter();
        var oldOut = Console.Out;
        Console.SetOut(stdout);
        try
        {
            ThreadCommand.RenderThreadEvents(new List<ThreadEvent>());
        }
        finally
        {
            Console.SetOut(oldOut);
        }

        stdout.ToString().ShouldContain("no events");
    }
}