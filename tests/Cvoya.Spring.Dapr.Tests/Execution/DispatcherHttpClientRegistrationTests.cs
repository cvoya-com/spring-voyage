// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net.Http;

using Cvoya.Spring.Dapr.DependencyInjection;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

/// <summary>
/// Regression tests for the worker-side <see cref="HttpClient"/> the
/// <see cref="DispatcherClientContainerRuntime"/> uses to talk to
/// <c>spring-dispatcher</c>.
/// </summary>
/// <remarks>
/// <para>
/// The shipping default of <see cref="HttpClient.Timeout"/> is 100 s. A
/// synchronous container run (<c>POST /v1/containers</c>) for a real
/// agent turn — Claude Code, Codex, etc. — routinely exceeds that. The
/// dispatcher already enforces the per-run deadline via
/// <c>ContainerConfig.Timeout</c>, so the worker-side transport must
/// default to <see cref="System.Threading.Timeout.InfiniteTimeSpan"/>:
/// otherwise the worker times out first, the dispatcher sees the
/// connection abort, kills the container, and the user gets nothing
/// back. This bit Stage 2 of #1063 / #522 in production immediately
/// after the argv-quoting fix let containers actually start. These
/// tests guard against the regression coming back.
/// </para>
/// </remarks>
public class DispatcherHttpClientRegistrationTests
{
    [Fact]
    public void DefaultTimeout_IsInfinite_SoDispatcherOwnsTheRunDeadline()
    {
        var services = new ServiceCollection();
        services.AddOptions<DispatcherClientOptions>();
        services.AddDispatcherHttpClient();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(DispatcherClientContainerRuntime.HttpClientName);

        client.Timeout.ShouldBe(System.Threading.Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public void RequestTimeoutOverride_IsHonoured()
    {
        var services = new ServiceCollection();
        services.AddOptions<DispatcherClientOptions>()
            .Configure(o => o.RequestTimeout = TimeSpan.FromMinutes(5));
        services.AddDispatcherHttpClient();

        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpClientFactory>();
        var client = factory.CreateClient(DispatcherClientContainerRuntime.HttpClientName);

        client.Timeout.ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void DispatcherClientOptions_RequestTimeout_DefaultIsNull()
    {
        // Null sentinel is what the registration treats as "use the
        // dispatcher's deadline". Flipping the default to a concrete
        // TimeSpan would silently re-introduce the regression even with
        // the InfiniteTimeSpan path intact, so guard the contract here.
        var options = new DispatcherClientOptions();
        options.RequestTimeout.ShouldBeNull();
    }
}