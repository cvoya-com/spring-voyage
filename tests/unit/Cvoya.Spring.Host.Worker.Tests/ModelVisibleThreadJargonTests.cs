// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Worker.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Host.Worker.Composition;

using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Negative-pin guard for the internal-vs-model-visible <c>thread</c>
/// boundary (#3079). "Thread" is platform-internal jargon for the
/// participant-set identity (ADR-0030); it is permitted on the developer
/// SDK surface and in the container environment (e.g. <c>SPRING_THREAD_ID</c>),
/// but it MUST NOT appear in anything an agent runtime / LLM reads, because
/// the model mis-reads "thread" as the lifetime identifier of an interaction
/// (the #3072 / #3077 mis-key). The model keys "this exchange" on
/// <c>message_id</c> and "who" on the participant set; where a runtime
/// correlation id is genuinely needed it is the A2A <c>context_id</c>, never
/// a Spring Voyage <c>thread_id</c>.
/// </summary>
/// <remarks>
/// The two surfaces a model reads from the tool layer are (1) the
/// <see cref="ToolDefinition.Description"/> + <see cref="ToolDefinition.InputSchema"/>
/// returned by <c>tools/list</c> / <c>sv.tools.list</c>, and (2) the
/// per-category <see cref="PlatformToolCategory.Summary"/> +
/// <see cref="PlatformToolCategory.UsageGuidance"/> returned by
/// <c>sv.tools.list_categories</c> / <c>sv.tools.list</c>. The platform
/// prompt is pinned separately (<c>PlatformPromptProvider</c> tests). This
/// test fails the build if any future edit reintroduces "thread" into either
/// surface.
/// </remarks>
public class ModelVisibleThreadJargonTests
{
    [Fact]
    public void NoRegisteredToolDescriptionOrSchema_MentionsThread()
    {
        using var provider = BuildWorkerServiceProvider();
        var registries = provider.GetServices<ISkillRegistry>().ToList();

        var offenders = new List<string>();
        foreach (var registry in registries)
        {
            foreach (var tool in registry.GetToolDefinitions())
            {
                if (MentionsThread(tool.Description))
                {
                    offenders.Add($"{tool.Name}: description → \"{tool.Description}\"");
                }

                var schema = tool.InputSchema.ValueKind == JsonValueKind.Undefined
                    ? string.Empty
                    : tool.InputSchema.GetRawText();
                if (MentionsThread(schema))
                {
                    offenders.Add($"{tool.Name}: input schema mentions \"thread\"");
                }
            }
        }

        offenders.ShouldBeEmpty(
            "Model-visible tool descriptions / input schemas must not contain the platform-internal " +
            "word \"thread\" (#3079) — an agent runtime mis-reads it as a per-interaction id. Use " +
            "\"conversation\" / participant-set language; key per-exchange identity on `message_id`:" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void NoCategorySummaryOrGuidance_MentionsThread()
    {
        var offenders = new List<string>();
        foreach (var category in PlatformToolCatalog.Categories)
        {
            if (MentionsThread(category.Summary))
            {
                offenders.Add($"{category.Token}: summary → \"{category.Summary}\"");
            }

            if (MentionsThread(category.UsageGuidance))
            {
                offenders.Add($"{category.Token}: usage_guidance mentions \"thread\"");
            }
        }

        offenders.ShouldBeEmpty(
            "Model-visible PlatformToolCatalog category summary / usage_guidance must not contain " +
            "the platform-internal word \"thread\" (#3079). Use \"conversation\" / participant-set " +
            "language:" + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    /// <summary>
    /// Case-insensitive substring match on "thread" — deliberately broad so
    /// derived forms (<c>thread_id</c>, <c>threads</c>) are caught too. No
    /// model-visible string has a legitimate use of the token; the internal
    /// SDK / env surface (where "thread" is allowed) is not scanned here.
    /// </summary>
    private static bool MentionsThread(string? text) =>
        !string.IsNullOrEmpty(text) &&
        text.Contains("thread", StringComparison.OrdinalIgnoreCase);

    private static ServiceProvider BuildWorkerServiceProvider()
    {
        var builder = WebApplication.CreateBuilder();

        // Satisfy the #261 fail-fast ConnectionStrings:SpringDb check; the
        // in-memory swap below supersedes the value.
        builder.Configuration["ConnectionStrings:SpringDb"] =
            "Host=test;Database=test;Username=test;Password=test";

        builder.Services.AddWorkerServices(builder.Configuration);

        var dbContextDescriptors = builder.Services
            .Where(d => d.ServiceType == typeof(DbContextOptions<Cvoya.Spring.Dapr.Data.SpringDbContext>))
            .ToList();
        foreach (var descriptor in dbContextDescriptors)
        {
            builder.Services.Remove(descriptor);
        }
        builder.Services.AddDbContext<Cvoya.Spring.Dapr.Data.SpringDbContext>(opt =>
            opt.UseInMemoryDatabase($"thread-jargon-{Guid.NewGuid():N}"));

        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = false,
            ValidateOnBuild = false,
        });
    }
}
