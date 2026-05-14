// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests.Configuration;

using Cvoya.Spring.Core.Configuration;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the severity defaults of the <see cref="ConfigurationRequirementStatus"/>
/// factory helpers. The renderer-side mapping (portal / CLI) on top of these
/// values is exercised separately — these tests ensure the wire shape carries
/// the expected raw severity so downstream consumers don't drift.
/// </summary>
public class ConfigurationRequirementStatusTests
{
    [Fact]
    public void Met_DefaultsToInformationSeverity()
    {
        var status = ConfigurationRequirementStatus.Met();

        status.Status.ShouldBe(ConfigurationStatus.Met);
        status.Severity.ShouldBe(SeverityLevel.Information);
        status.Reason.ShouldBeNull();
        status.Suggestion.ShouldBeNull();
        status.FatalError.ShouldBeNull();
    }

    [Fact]
    public void MetWithWarning_CarriesWarningSeverity()
    {
        var status = ConfigurationRequirementStatus.MetWithWarning(
            reason: "ephemeral key",
            suggestion: "rotate before prod");

        status.Status.ShouldBe(ConfigurationStatus.Met);
        status.Severity.ShouldBe(SeverityLevel.Warning);
        status.Reason.ShouldBe("ephemeral key");
        status.Suggestion.ShouldBe("rotate before prod");
    }

    [Fact]
    public void Disabled_DefaultsToInformationSeverity_NotWarning()
    {
        // Issue #1747: an optional subsystem the operator deliberately left
        // unconfigured is informational — not a warning. The renderer adds
        // any visual escalation by consulting `IsMandatory`; the raw
        // severity must not pre-warn.
        var status = ConfigurationRequirementStatus.Disabled(
            reason: "Dispatcher:BaseUrl is not set.",
            suggestion: "Leave unset on hosts that don't drive delegated execution.");

        status.Status.ShouldBe(ConfigurationStatus.Disabled);
        status.Severity.ShouldBe(SeverityLevel.Information);
        status.Reason.ShouldBe("Dispatcher:BaseUrl is not set.");
        status.Suggestion.ShouldBe("Leave unset on hosts that don't drive delegated execution.");
    }

    [Fact]
    public void Invalid_CarriesErrorSeverity()
    {
        var fatal = new System.InvalidOperationException("kaboom");
        var status = ConfigurationRequirementStatus.Invalid(
            reason: "broken",
            suggestion: "fix it",
            fatalError: fatal);

        status.Status.ShouldBe(ConfigurationStatus.Invalid);
        status.Severity.ShouldBe(SeverityLevel.Error);
        status.FatalError.ShouldBeSameAs(fatal);
    }
}
