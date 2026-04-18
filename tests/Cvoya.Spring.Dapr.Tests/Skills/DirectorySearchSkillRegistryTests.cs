// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="DirectorySearchSkillRegistry"/> — the meta-skill
/// adapter that exposes <c>directory/search</c> on the same surface the
/// expertise-directory-driven skills use (#542). Covers tool-definition
/// advertising, argument parsing, and payload shaping.
/// </summary>
public class DirectorySearchSkillRegistryTests
{
    private readonly IExpertiseSearch _search = Substitute.For<IExpertiseSearch>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    public DirectorySearchSkillRegistryTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
    }

    private DirectorySearchSkillRegistry CreateRegistry() => new(_search, _loggerFactory);

    [Fact]
    public void GetToolDefinitions_AdvertisesDirectorySearch()
    {
        var registry = CreateRegistry();
        var tools = registry.GetToolDefinitions();

        tools.ShouldHaveSingleItem();
        tools[0].Name.ShouldBe("directory/search");
        tools[0].Description.ShouldNotBeNullOrWhiteSpace();
        // Input schema is a typed object — planners can validate arguments
        // against it before calling.
        tools[0].InputSchema.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public async Task InvokeAsync_UnknownSkillName_ThrowsSkillNotFound()
    {
        var registry = CreateRegistry();
        var args = JsonDocument.Parse("{}").RootElement;

        await Should.ThrowAsync<SkillNotFoundException>(
            async () => await registry.InvokeAsync("expertise/python", args, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvokeAsync_CallsSearchAndShapesPayload()
    {
        var registry = CreateRegistry();
        _search.SearchAsync(Arg.Any<ExpertiseSearchQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ExpertiseSearchResult(
                new[]
                {
                    new ExpertiseSearchHit(
                        Slug: "python",
                        Domain: new ExpertiseDomain("python", "Python expertise", ExpertiseLevel.Advanced, "{\"type\":\"object\"}"),
                        Owner: new Address("unit", "eng"),
                        OwnerDisplayName: "Engineering",
                        AggregatingUnit: null,
                        TypedContract: true,
                        Score: 100,
                        MatchReason: "exact slug"),
                },
                TotalCount: 1,
                Limit: 50,
                Offset: 0));

        var args = JsonDocument.Parse("""{ "text": "python", "typedOnly": true }""").RootElement;
        var payload = await registry.InvokeAsync("directory/search", args, TestContext.Current.CancellationToken);

        payload.GetProperty("totalCount").GetInt32().ShouldBe(1);
        payload.GetProperty("limit").GetInt32().ShouldBe(50);
        var hit = payload.GetProperty("hits").EnumerateArray().First();
        hit.GetProperty("slug").GetString().ShouldBe("python");
        hit.GetProperty("skill").GetString().ShouldBe("expertise/python");
        hit.GetProperty("owner").GetString().ShouldBe("unit://eng");
        hit.GetProperty("typedContract").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void ParseArguments_AcceptsStringAddressForOwner()
    {
        var args = JsonDocument.Parse("""{ "owner": "unit://ada-squad" }""").RootElement;
        var query = DirectorySearchSkillRegistry.ParseArguments(args);

        query.Owner.ShouldNotBeNull();
        query.Owner!.Scheme.ShouldBe("unit");
        query.Owner.Path.ShouldBe("ada-squad");
    }

    [Fact]
    public void ParseArguments_AcceptsObjectAddressForOwner()
    {
        var args = JsonDocument.Parse("""{ "owner": { "scheme": "agent", "path": "ada" } }""").RootElement;
        var query = DirectorySearchSkillRegistry.ParseArguments(args);

        query.Owner.ShouldNotBeNull();
        query.Owner!.Scheme.ShouldBe("agent");
        query.Owner.Path.ShouldBe("ada");
    }

    [Fact]
    public void ParseArguments_EmptyObject_ReturnsDefaults()
    {
        var args = JsonDocument.Parse("{}").RootElement;
        var query = DirectorySearchSkillRegistry.ParseArguments(args);

        query.Text.ShouldBeNull();
        query.Owner.ShouldBeNull();
        query.Domains.ShouldBeNull();
        query.TypedOnly.ShouldBeFalse();
        query.Limit.ShouldBe(ExpertiseSearchQuery.DefaultLimit);
        query.Offset.ShouldBe(0);
    }

    [Fact]
    public void ParseArguments_InsideUnit_SetsInternalContext()
    {
        var args = JsonDocument.Parse("""{ "insideUnit": true }""").RootElement;
        var query = DirectorySearchSkillRegistry.ParseArguments(args);

        query.Context.ShouldNotBeNull();
        query.Context!.Internal.ShouldBeTrue();
    }
}