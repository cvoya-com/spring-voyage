// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Configuration;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.Configuration;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the #616 startup configuration validator.
/// </summary>
public class StartupConfigurationValidatorTests
{
    [Fact]
    public async Task StartAsync_NoRequirements_ProducesEmptyHealthyReport()
    {
        var validator = new StartupConfigurationValidator(
            Array.Empty<IConfigurationRequirement>(),
            NullLogger<StartupConfigurationValidator>.Instance);

        await validator.StartAsync(TestContext.Current.CancellationToken);

        validator.Report.Status.ShouldBe(ConfigurationReportStatus.Healthy);
        validator.Report.Subsystems.ShouldBeEmpty();
    }

    [Fact]
    public async Task StartAsync_AllMet_ReturnsHealthy()
    {
        var validator = new StartupConfigurationValidator(
            new[]
            {
                FakeRequirement.Create("r1", "Sub-A", isMandatory: true, status: ConfigurationRequirementStatus.Met()),
                FakeRequirement.Create("r2", "Sub-A", isMandatory: false, status: ConfigurationRequirementStatus.Met()),
                FakeRequirement.Create("r3", "Sub-B", isMandatory: true, status: ConfigurationRequirementStatus.Met()),
            },
            NullLogger<StartupConfigurationValidator>.Instance);

        await validator.StartAsync(TestContext.Current.CancellationToken);

        validator.Report.Status.ShouldBe(ConfigurationReportStatus.Healthy);
        validator.Report.Subsystems.Count.ShouldBe(2);
    }

    [Fact]
    public async Task StartAsync_OptionalDisabled_ReturnsDegraded()
    {
        var validator = new StartupConfigurationValidator(
            new[]
            {
                FakeRequirement.Create("r1", "Sub-A", isMandatory: true, status: ConfigurationRequirementStatus.Met()),
                FakeRequirement.Create("r2", "Sub-B", isMandatory: false,
                    status: ConfigurationRequirementStatus.Disabled("not configured", "set the env var")),
            },
            NullLogger<StartupConfigurationValidator>.Instance);

        await validator.StartAsync(TestContext.Current.CancellationToken);

        validator.Report.Status.ShouldBe(ConfigurationReportStatus.Degraded);
        var subB = validator.Report.Subsystems.Single(s => s.SubsystemName == "Sub-B");
        subB.Requirements[0].Status.ShouldBe(ConfigurationStatus.Disabled);
        subB.Requirements[0].Suggestion.ShouldBe("set the env var");
    }

    [Fact]
    public async Task StartAsync_MetWithWarning_ReturnsDegraded()
    {
        // The "met but degraded" path — ephemeral dev key, default endpoint, etc.
        var validator = new StartupConfigurationValidator(
            new[]
            {
                FakeRequirement.Create("r1", "Sub-A", isMandatory: true,
                    status: ConfigurationRequirementStatus.MetWithWarning("ephemeral key", "fix in prod")),
            },
            NullLogger<StartupConfigurationValidator>.Instance);

        await validator.StartAsync(TestContext.Current.CancellationToken);

        validator.Report.Status.ShouldBe(ConfigurationReportStatus.Degraded);
        validator.Report.Subsystems[0].Requirements[0].Severity.ShouldBe(SeverityLevel.Warning);
    }

    [Fact]
    public async Task StartAsync_MandatoryInvalid_Throws()
    {
        var fatal = new InvalidOperationException("boom");
        var validator = new StartupConfigurationValidator(
            new[]
            {
                FakeRequirement.Create("r1", "Sub-A", isMandatory: true,
                    status: ConfigurationRequirementStatus.Invalid("broken", "fix it", fatal)),
            },
            NullLogger<StartupConfigurationValidator>.Instance);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => validator.StartAsync(TestContext.Current.CancellationToken));
        ex.ShouldBeSameAs(fatal);
    }

    [Fact]
    public async Task StartAsync_MultipleMandatoryInvalid_ThrowsAggregate()
    {
        var validator = new StartupConfigurationValidator(
            new[]
            {
                FakeRequirement.Create("r1", "Sub-A", isMandatory: true,
                    status: ConfigurationRequirementStatus.Invalid("a-broken", null, new InvalidOperationException("a"))),
                FakeRequirement.Create("r2", "Sub-B", isMandatory: true,
                    status: ConfigurationRequirementStatus.Invalid("b-broken", null, new InvalidOperationException("b"))),
            },
            NullLogger<StartupConfigurationValidator>.Instance);

        var ex = await Should.ThrowAsync<AggregateException>(
            () => validator.StartAsync(TestContext.Current.CancellationToken));
        ex.InnerExceptions.Count.ShouldBe(2);
    }

    [Fact]
    public async Task StartAsync_OptionalInvalid_DoesNotThrow_ReportStatusFailed()
    {
        // Optional subsystem misconfigured → don't kill the host; surface it.
        var validator = new StartupConfigurationValidator(
            new[]
            {
                FakeRequirement.Create("r1", "Sub-A", isMandatory: false,
                    status: ConfigurationRequirementStatus.Invalid("broken", "fix it")),
            },
            NullLogger<StartupConfigurationValidator>.Instance);

        await validator.StartAsync(TestContext.Current.CancellationToken);

        validator.Report.Status.ShouldBe(ConfigurationReportStatus.Failed);
        validator.Report.Subsystems[0].Requirements[0].Status.ShouldBe(ConfigurationStatus.Invalid);
    }

    [Fact]
    public async Task StartAsync_ValidatorThrows_MarksInvalidWithFatalError()
    {
        // The validator contract says validators catch their own exceptions.
        // When they don't, the framework wraps the throw in Invalid(...) and
        // (for mandatory requirements) aborts host startup with the wrapped
        // exception.
        var validator = new StartupConfigurationValidator(
            new[] { new ThrowingRequirement(isMandatory: true) },
            NullLogger<StartupConfigurationValidator>.Instance);

        await Should.ThrowAsync<InvalidOperationException>(
            () => validator.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StartAsync_MandatoryInvalidWithoutFatal_ThrowsGenericInvalidOp()
    {
        // The fatalError on ConfigurationRequirementStatus is optional — when
        // a requirement reports Invalid without one we synthesise a clear
        // InvalidOperationException so the host still fails fast.
        var validator = new StartupConfigurationValidator(
            new[]
            {
                FakeRequirement.Create("r1", "Sub-A", isMandatory: true,
                    status: ConfigurationRequirementStatus.Invalid("broken", "fix it", fatalError: null)),
            },
            NullLogger<StartupConfigurationValidator>.Instance);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => validator.StartAsync(TestContext.Current.CancellationToken));
        ex.ShouldNotBeNull();
        ex.Message.ShouldContain("r1");
        ex.Message.ShouldContain("broken");
    }

    private sealed class FakeRequirement : IConfigurationRequirement
    {
        public static FakeRequirement Create(
            string id, string subsystem, bool isMandatory,
            ConfigurationRequirementStatus status) => new()
            {
                RequirementId = id,
                DisplayName = id,
                SubsystemName = subsystem,
                IsMandatory = isMandatory,
                Description = "test",
                Result = status,
            };

        public required string RequirementId { get; init; }
        public required string DisplayName { get; init; }
        public required string SubsystemName { get; init; }
        public required bool IsMandatory { get; init; }
        public IReadOnlyList<string> EnvironmentVariableNames { get; init; } = Array.Empty<string>();
        public string? ConfigurationSectionPath { get; init; }
        public required string Description { get; init; }
        public Uri? DocumentationUrl { get; init; }
        public required ConfigurationRequirementStatus Result { get; init; }

        public Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result);
    }

    private sealed class ThrowingRequirement(bool isMandatory) : IConfigurationRequirement
    {
        public string RequirementId => "thrower";
        public string DisplayName => "Thrower";
        public string SubsystemName => "Bad";
        public bool IsMandatory { get; } = isMandatory;
        public IReadOnlyList<string> EnvironmentVariableNames { get; } = Array.Empty<string>();
        public string? ConfigurationSectionPath => null;
        public string Description => "validator throws";
        public Uri? DocumentationUrl => null;

        public Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("kaboom");
    }
}