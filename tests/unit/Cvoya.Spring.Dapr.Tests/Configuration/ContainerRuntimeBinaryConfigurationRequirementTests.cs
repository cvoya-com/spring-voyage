// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Configuration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

public class ContainerRuntimeBinaryConfigurationRequirementTests
{
    private static IOptions<ContainerRuntimeOptions> Opts(string runtime) =>
        Options.Create(new ContainerRuntimeOptions { RuntimeType = runtime });

    [Fact]
    public async Task ValidateAsync_BinaryResolves_ReturnsMet()
    {
        var probe = new FakeProbe(binaryName => $"/usr/local/bin/{binaryName}");
        var requirement = new ContainerRuntimeBinaryConfigurationRequirement(Opts("podman"), probe);

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Met);
        probe.LastBinaryName.ShouldBe("podman");
    }

    [Fact]
    public async Task ValidateAsync_BinaryMissing_ReturnsInvalidWithFatalError()
    {
        var probe = new FakeProbe(_ => null);
        var requirement = new ContainerRuntimeBinaryConfigurationRequirement(Opts("podman"), probe);

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.Severity.ShouldBe(SeverityLevel.Error);
        status.Reason!.ShouldContain("'podman'");
        status.Reason!.ShouldContain("PATH");
        status.Suggestion!.ShouldContain("podman-remote");
        status.FatalError.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public async Task ValidateAsync_EmptyRuntimeType_ReturnsDisabled()
    {
        var probe = new FakeProbe(_ => throw new InvalidOperationException("should not be called"));
        var requirement = new ContainerRuntimeBinaryConfigurationRequirement(Opts(""), probe);

        var status = await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        status.Status.ShouldBe(ConfigurationStatus.Disabled);
        probe.LastBinaryName.ShouldBeNull();
    }

    [Fact]
    public async Task ValidateAsync_RuntimeTypeCaseInsensitive_ProbesLowerCase()
    {
        var probe = new FakeProbe(binaryName => $"/usr/local/bin/{binaryName}");
        var requirement = new ContainerRuntimeBinaryConfigurationRequirement(Opts("PODMAN"), probe);

        await requirement.ValidateAsync(TestContext.Current.CancellationToken);

        probe.LastBinaryName.ShouldBe("podman");
    }

    [Fact]
    public async Task ValidateAsync_ProbeTimesOut_ReturnsInvalidFatal()
    {
        // Simulate a probe that takes too long by throwing the exact shape the
        // requirement catches — a cooperative OperationCanceledException.
        var probe = new FakeProbe(_ => throw new OperationCanceledException());
        var requirement = new ContainerRuntimeBinaryConfigurationRequirement(Opts("podman"), probe);

        var status = await requirement.ValidateAsync(CancellationToken.None);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.Reason!.ShouldContain("timed out");
        status.FatalError.ShouldNotBeNull();
    }

    [Fact]
    public void RequirementMetadata_IsStableAndMandatory()
    {
        var requirement = new ContainerRuntimeBinaryConfigurationRequirement(
            Opts("podman"),
            new FakeProbe(_ => null));

        requirement.RequirementId.ShouldBe("container-runtime-binary");
        requirement.SubsystemName.ShouldBe("Container Runtime");
        requirement.IsMandatory.ShouldBeTrue();
        requirement.EnvironmentVariableNames.ShouldContain("PATH");
        requirement.ConfigurationSectionPath.ShouldBe("ContainerRuntime");
        requirement.DocumentationUrl.ShouldNotBeNull();
    }

    [Fact]
    public void ContainerRuntimeBinaryProbe_WalksPath_FindsExistingFile()
    {
        // The probe takes injectable PATH and file-existence lambdas so this
        // test can run without mutating the process environment (which would
        // race against other tests in the same assembly).
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var separator = isWindows ? ";" : ":";
        var path = string.Join(separator, "/opt/first", "/opt/second", "/opt/third");
        var hit = Path.Combine("/opt/second", "podman");
        var existing = new HashSet<string>(new[] { hit }, StringComparer.Ordinal);

        var probe = new ContainerRuntimeBinaryProbe(() => path, existing.Contains);
        var resolved = probe.TryResolveBinary("podman", CancellationToken.None);

        resolved.ShouldBe(hit);
    }

    [Fact]
    public void ContainerRuntimeBinaryProbe_MissingFromPath_ReturnsNull()
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var separator = isWindows ? ";" : ":";
        var path = string.Join(separator, "/opt/empty", "/opt/also-empty");

        var probe = new ContainerRuntimeBinaryProbe(() => path, _ => false);
        var resolved = probe.TryResolveBinary("podman", CancellationToken.None);

        resolved.ShouldBeNull();
    }

    [Fact]
    public void ContainerRuntimeBinaryProbe_EmptyPath_ReturnsNull()
    {
        var probe = new ContainerRuntimeBinaryProbe(() => string.Empty, _ => true);
        var resolved = probe.TryResolveBinary("podman", CancellationToken.None);

        resolved.ShouldBeNull();
    }

    private sealed class FakeProbe : IContainerRuntimeBinaryProbe
    {
        private readonly Func<string, string?> _resolver;

        public FakeProbe(Func<string, string?> resolver)
        {
            _resolver = resolver;
        }

        public string? LastBinaryName { get; private set; }

        public string? TryResolveBinary(string binaryName, CancellationToken cancellationToken)
        {
            LastBinaryName = binaryName;
            return _resolver(binaryName);
        }
    }
}
