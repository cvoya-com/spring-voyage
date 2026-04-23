// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Configuration;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for <c>GET /api/v1/system/configuration</c> (#616).
/// Verifies the endpoint surfaces the cached <see cref="ConfigurationReport"/>
/// verbatim so the portal and CLI render identical data.
/// </summary>
public class SystemConfigurationEndpointsTests
{
    [Fact]
    public async Task GetConfiguration_ReturnsReport()
    {
        var report = new ConfigurationReport(
            ConfigurationReportStatus.Degraded,
            DateTimeOffset.UtcNow,
            new[]
            {
                new SubsystemConfigurationReport(
                    "Database",
                    ConfigurationReportStatus.Healthy,
                    new[]
                    {
                        new RequirementStatus(
                            RequirementId: "database-connection-string",
                            DisplayName: "Database connection string",
                            Description: "PostgreSQL",
                            IsMandatory: true,
                            Status: ConfigurationStatus.Met,
                            Severity: SeverityLevel.Information,
                            Reason: null,
                            Suggestion: null,
                            EnvironmentVariableNames: new[] { "ConnectionStrings__SpringDb" },
                            ConfigurationSectionPath: "ConnectionStrings:SpringDb",
                            DocumentationUrl: null),
                    }),
                new SubsystemConfigurationReport(
                    "GitHub Connector",
                    ConfigurationReportStatus.Degraded,
                    new[]
                    {
                        new RequirementStatus(
                            RequirementId: "github-app-credentials",
                            DisplayName: "GitHub App credentials",
                            Description: "App auth",
                            IsMandatory: false,
                            Status: ConfigurationStatus.Disabled,
                            Severity: SeverityLevel.Warning,
                            Reason: "GitHub App not configured.",
                            Suggestion: "Run `spring github-app register`.",
                            EnvironmentVariableNames: new[] { "GitHub__AppId", "GitHub__PrivateKeyPem" },
                            ConfigurationSectionPath: "GitHub",
                            DocumentationUrl: null),
                    }),
            });

        await using var factory = new CustomWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Replace the validator singleton with one that returns our
                    // scripted report without running StartAsync.
                    services.RemoveAll<IStartupConfigurationValidator>();
                    services.AddSingleton<IStartupConfigurationValidator>(
                        new StubValidator(report));
                });
            });

        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/system/configuration", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        // Anonymous endpoint — no token needed.
        var json = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };
        var body = await response.Content.ReadFromJsonAsync<ConfigurationReport>(json, ct);
        body.ShouldNotBeNull();
        body!.Status.ShouldBe(ConfigurationReportStatus.Degraded);
        body.Subsystems.Count.ShouldBe(2);
        var suggestion = body.Subsystems[1].Requirements[0].Suggestion;
        suggestion.ShouldNotBeNull();
        suggestion!.ShouldContain("spring github-app register");
    }

    private sealed class StubValidator(ConfigurationReport report) : IStartupConfigurationValidator
    {
        public ConfigurationReport Report { get; } = report;
    }
}