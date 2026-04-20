// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System;
using System.Linq;

using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Shouldly;

using Xunit;

/// <summary>
/// Regression coverage for #711. Phase 2.8 (#682) dropped the legacy
/// <c>AddCvoyaSpringOllamaLlm</c> call from <c>Program.cs</c>, which silently
/// broke two host-side bindings: <c>IOptions&lt;OllamaOptions&gt;</c> (consumed
/// by <c>OllamaEndpoints</c>/<c>SystemEndpoints</c>) and the
/// <c>OllamaConfigurationRequirement</c> startup probe. These tests pin the
/// restored bindings so a future refactor can't regress them without noticing.
/// </summary>
public class OllamaLegacyBindingTests
{
    [Fact]
    public void OllamaOptions_BindsFromLanguageModelOllamaSection()
    {
        using var factory = CreateFactory(overrides =>
        {
            overrides.UseSetting("LanguageModel:Ollama:BaseUrl", "http://ollama.test:11434");
            overrides.UseSetting("LanguageModel:Ollama:HealthCheckTimeoutSeconds", "7");
        });

        using var scope = factory.Services.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<OllamaOptions>>();

        options.Value.BaseUrl.ShouldBe("http://ollama.test:11434");
        options.Value.HealthCheckTimeoutSeconds.ShouldBe(7);
    }

    [Fact]
    public void OllamaConfigurationRequirement_IsRegisteredInApiHost()
    {
        using var factory = CreateFactory(overrides =>
        {
            // Enable the legacy path so AddCvoyaSpringOllamaLlm registers the
            // requirement — otherwise it short-circuits after binding
            // OllamaOptions. Matches the production deployment when an
            // operator flips LanguageModel__Ollama__Enabled=true.
            overrides.UseSetting("LanguageModel:Ollama:Enabled", "true");
        });

        using var scope = factory.Services.CreateScope();
        var requirements = scope.ServiceProvider
            .GetServices<IConfigurationRequirement>()
            .ToList();

        requirements.ShouldContain(r => r is OllamaConfigurationRequirement,
            "The Ollama reachability probe must surface in the startup configuration report (#616).");
    }

    private static WebApplicationFactory<Program> CreateFactory(Action<IWebHostBuilder> configure)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Use --local so the host skips the ApiToken auth path that
                // WebApplicationFactory tests otherwise have to stub.
                builder.UseSetting("LocalDev", "true");
                builder.UseSetting("ConnectionStrings:SpringDb",
                    "Host=test;Database=test;Username=test;Password=test");
                builder.UseSetting("Secrets:AllowEphemeralDevKey", "true");

                configure(builder);

                builder.ConfigureServices(services =>
                {
                    // Swap the real Postgres DbContext for in-memory so the
                    // factory boots without a live database.
                    var dbDescriptors = services
                        .Where(d => d.ServiceType == typeof(DbContextOptions<SpringDbContext>)
                                 || d.ServiceType == typeof(DbContextOptions)
                                 || d.ServiceType == typeof(SpringDbContext)
                                 || (d.ServiceType.FullName?.StartsWith(
                                        "Microsoft.EntityFrameworkCore.", StringComparison.Ordinal) ?? false)
                                 || (d.ServiceType.FullName?.StartsWith(
                                        "Npgsql.", StringComparison.Ordinal) ?? false))
                        .ToList();

                    foreach (var descriptor in dbDescriptors)
                    {
                        services.Remove(descriptor);
                    }

                    services.AddDbContext<SpringDbContext>(options =>
                        options.UseInMemoryDatabase($"OllamaLegacyBinding_{Guid.NewGuid()}"));
                });
            });
    }
}