// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tenancy;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.Extensions.Logging;

using Shouldly;

using Xunit;

public class TenantScopeBypassTests
{
    [Fact]
    public void IsBypassActive_Defaults_To_False()
    {
        var sut = CreateSut();
        sut.IsBypassActive.ShouldBeFalse();
    }

    [Fact]
    public void BeginBypass_Activates_And_Dispose_Deactivates()
    {
        var sut = CreateSut();

        using (var scope = sut.BeginBypass("unit test"))
        {
            scope.ShouldNotBeNull();
            sut.IsBypassActive.ShouldBeTrue();
        }

        sut.IsBypassActive.ShouldBeFalse();
    }

    [Fact]
    public void NestedScopes_StayActive_Until_All_Disposed()
    {
        var sut = CreateSut();

        var outer = sut.BeginBypass("outer");
        sut.IsBypassActive.ShouldBeTrue();

        var inner = sut.BeginBypass("inner");
        sut.IsBypassActive.ShouldBeTrue();

        inner.Dispose();
        sut.IsBypassActive.ShouldBeTrue("outer scope is still live");

        outer.Dispose();
        sut.IsBypassActive.ShouldBeFalse("all scopes disposed");
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var sut = CreateSut();

        var outer = sut.BeginBypass("outer");
        var inner = sut.BeginBypass("inner");

        // Double-disposing the inner scope must not corrupt the outer.
        inner.Dispose();
        inner.Dispose();

        sut.IsBypassActive.ShouldBeTrue("double-dispose of inner must not decrement outer");

        outer.Dispose();
        sut.IsBypassActive.ShouldBeFalse();
    }

    [Fact]
    public void BeginBypass_Rejects_NullOrWhitespace_Reason()
    {
        var sut = CreateSut();

        Should.Throw<ArgumentException>(() => sut.BeginBypass(null!));
        Should.Throw<ArgumentException>(() => sut.BeginBypass(""));
        Should.Throw<ArgumentException>(() => sut.BeginBypass("   "));

        // Failed BeginBypass must not have incremented the depth counter.
        sut.IsBypassActive.ShouldBeFalse();
    }

    [Fact]
    public void BeginBypass_Emits_InformationLog_With_Reason()
    {
        var captured = new CapturingLogger<TenantScopeBypass>();
        var sut = new TenantScopeBypass(captured);

        using (sut.BeginBypass("database migration"))
        {
            // open-log captured
        }

        // One entry for open, one for close.
        captured.Entries.Count.ShouldBe(2);
        captured.Entries.ShouldAllBe(e => e.Level == LogLevel.Information);

        var openMessage = captured.Entries[0].Message;
        openMessage.ShouldContain("database migration");
        openMessage.ShouldContain("opened");

        var closeMessage = captured.Entries[1].Message;
        closeMessage.ShouldContain("database migration");
        closeMessage.ShouldContain("closed");
    }

    [Fact]
    public async Task IsBypassActive_Flows_Across_Await()
    {
        var sut = CreateSut();

        using (sut.BeginBypass("async test"))
        {
            sut.IsBypassActive.ShouldBeTrue();
            await Task.Yield();
            sut.IsBypassActive.ShouldBeTrue("AsyncLocal must flow across await");

            await Task.Run(
                () =>
                {
                    // A child Task.Run inherits the ExecutionContext, so
                    // AsyncLocal<T> values from the caller are visible.
                    sut.IsBypassActive.ShouldBeTrue("Task.Run inherits async context");
                },
                TestContext.Current.CancellationToken);
        }

        sut.IsBypassActive.ShouldBeFalse();
    }

    [Fact]
    public async Task Parallel_Flows_Do_Not_See_Each_Others_Bypass()
    {
        var sut = CreateSut();

        // Kick off two independent async flows via Task.Run. Each flow
        // opens its own scope, checks visibility, and returns the
        // result. Because AsyncLocal partitions per logical flow, flow A
        // must not see flow B's scope.
        // Use an event to synchronise the two flows: A opens its scope
        // and then signals; B waits for the signal and then checks
        // visibility. AsyncLocal partitions per logical flow, so A's
        // scope must not leak into B even though both run on the same
        // thread pool.
        using var aOpened = new System.Threading.ManualResetEventSlim(false);
        using var bObserved = new System.Threading.ManualResetEventSlim(false);

        var flowA = Task.Run(() =>
        {
            using (sut.BeginBypass("flow-A"))
            {
                aOpened.Set();
                bObserved.Wait(TestContext.Current.CancellationToken);
                return sut.IsBypassActive;
            }
        });

        var flowB = Task.Run(() =>
        {
            aOpened.Wait(TestContext.Current.CancellationToken);
            // At this point flow A has opened a scope; flow B's copy of
            // the AsyncLocal is still zero because the scope was opened
            // in flow A's execution-context branch, not B's.
            var observed = sut.IsBypassActive;
            bObserved.Set();
            return observed;
        });

        var results = await Task.WhenAll(flowA, flowB);
        results[0].ShouldBeTrue("flow A saw its own scope");
        results[1].ShouldBeFalse("flow B did not see flow A's scope");

        sut.IsBypassActive.ShouldBeFalse("ambient flow is untouched");
    }

    private static TenantScopeBypass CreateSut()
        => new(new CapturingLogger<TenantScopeBypass>());

    // Minimal ILogger<T> that records every entry without needing a
    // mocking framework for log assertions. Kept internal to the test
    // fixture.
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _entries.Enqueue(new LogEntry(logLevel, eventId, formatter(state, exception)));
        }
    }

    private sealed record LogEntry(LogLevel Level, EventId EventId, string Message);
}