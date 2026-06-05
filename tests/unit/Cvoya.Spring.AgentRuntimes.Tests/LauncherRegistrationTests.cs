// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Tests;

using Cvoya.Spring.AgentRuntimes.DependencyInjection;
using Cvoya.Spring.AgentRuntimes.Launchers;
using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Regression tests for <see cref="ServiceCollectionExtensions.AddCvoyaSpringAgentRuntimes"/>
/// — the launcher DI registration that ADR-0038 Chunk 2a moved out of
/// <c>AddCvoyaSpringDapr</c>.
/// </summary>
public class LauncherRegistrationTests
{
    [Fact]
    public void AddCvoyaSpringAgentRuntimes_RegistersAllLaunchers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCvoyaSpringAgentRuntimes();

        using var provider = services.BuildServiceProvider();

        var launchers = provider.GetServices<IAgentRuntimeLauncher>().ToList();
        var launchersByKind = launchers.ToDictionary(l => l.Kind, StringComparer.OrdinalIgnoreCase);

        launchersByKind.ShouldContainKey(LauncherIds.ClaudeCodeCli);
        launchersByKind[LauncherIds.ClaudeCodeCli].ShouldBeOfType<ClaudeCodeLauncher>();

        launchersByKind.ShouldContainKey(LauncherIds.CodexCli);
        launchersByKind[LauncherIds.CodexCli].ShouldBeOfType<CodexLauncher>();

        launchersByKind.ShouldContainKey(LauncherIds.GeminiCli);
        launchersByKind[LauncherIds.GeminiCli].ShouldBeOfType<GeminiLauncher>();

        launchersByKind.ShouldContainKey(LauncherIds.SpringVoyageAgent);
        launchersByKind[LauncherIds.SpringVoyageAgent].ShouldBeOfType<SpringVoyageAgentLauncher>();

        launchersByKind.ShouldContainKey(LauncherIds.A2AProcess);
        launchersByKind[LauncherIds.A2AProcess].ShouldBeOfType<A2AProcessLauncher>();
    }

    [Fact]
    public void AddCvoyaSpringAgentRuntimes_RegistersLauncherRegistry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCvoyaSpringAgentRuntimes();

        using var provider = services.BuildServiceProvider();

        var registry = provider.GetRequiredService<IAgentRuntimeLauncherRegistry>();

        registry.Get(LauncherIds.ClaudeCodeCli).ShouldBeOfType<ClaudeCodeLauncher>();
        registry.Get(LauncherIds.CodexCli).ShouldBeOfType<CodexLauncher>();
        registry.Get(LauncherIds.GeminiCli).ShouldBeOfType<GeminiLauncher>();
        registry.Get(LauncherIds.SpringVoyageAgent).ShouldBeOfType<SpringVoyageAgentLauncher>();
        registry.Get(LauncherIds.A2AProcess).ShouldBeOfType<A2AProcessLauncher>();
        registry.Get("does-not-exist").ShouldBeNull();
    }
}
