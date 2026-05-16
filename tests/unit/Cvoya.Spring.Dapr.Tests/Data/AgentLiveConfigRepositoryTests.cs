// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="AgentLiveConfigRepository"/> (#2048 / ADR-0040).
/// Verifies the upsert / read paths against the EF in-memory provider —
/// the integration suite exercises the same surface against Postgres.
/// These fast tests pin the contract that the actor / coordinator
/// rely on: partial PATCH semantics, replace-in-full skill / expertise
/// writes, and the cross-restart "is expertise initialised?" flag.
/// </summary>
public class AgentLiveConfigRepositoryTests : IDisposable
{
    private static readonly Guid Agent1 = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid Agent2 = new("aaaaaaaa-0000-0000-0000-000000000002");

    private readonly SpringDbContext _context;
    private readonly AgentLiveConfigRepository _repository;

    public AgentLiveConfigRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new SpringDbContext(options);
        _repository = new AgentLiveConfigRepository(_context);
    }

    [Fact]
    public async Task GetMetadataAsync_NoRow_ReturnsAllNullMetadata()
    {
        var ct = TestContext.Current.CancellationToken;

        var fetched = await _repository.GetMetadataAsync(Agent1, ct);

        fetched.Model.ShouldBeNull();
        fetched.Specialty.ShouldBeNull();
        fetched.Enabled.ShouldBeNull();
        fetched.ExecutionMode.ShouldBeNull();
        fetched.ParentUnit.ShouldBeNull();
    }

    [Fact]
    public async Task UpsertMetadataAsync_AllFields_PersistsAndRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;

        var written = await _repository.UpsertMetadataAsync(
            Agent1,
            new AgentMetadata(
                Model: "claude-opus",
                Specialty: "reviewer",
                Enabled: false,
                ExecutionMode: AgentExecutionMode.OnDemand),
            ct);

        written.Count.ShouldBe(4);

        var fetched = await _repository.GetMetadataAsync(Agent1, ct);
        fetched.Model.ShouldBe("claude-opus");
        fetched.Specialty.ShouldBe("reviewer");
        fetched.Enabled.ShouldBe(false);
        fetched.ExecutionMode.ShouldBe(AgentExecutionMode.OnDemand);
    }

    [Fact]
    public async Task UpsertMetadataAsync_PartialPatch_LeavesUnsetFieldsAlone()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertMetadataAsync(
            Agent1,
            new AgentMetadata(Model: "gpt-4o", Specialty: "reviewer", Enabled: true),
            ct);

        var written = await _repository.UpsertMetadataAsync(
            Agent1,
            new AgentMetadata(Specialty: "implementer"),
            ct);

        written.Count.ShouldBe(1);
        written.ShouldContain("Specialty");

        var fetched = await _repository.GetMetadataAsync(Agent1, ct);
        fetched.Model.ShouldBe("gpt-4o");
        fetched.Specialty.ShouldBe("implementer");
        fetched.Enabled.ShouldBe(true);
    }

    [Fact]
    public async Task UpsertMetadataAsync_AllFieldsNull_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;

        var written = await _repository.UpsertMetadataAsync(
            Agent1, new AgentMetadata(), ct);

        written.ShouldBeEmpty();

        var fetched = await _repository.GetMetadataAsync(Agent1, ct);
        fetched.Model.ShouldBeNull();
        fetched.Specialty.ShouldBeNull();
    }

    [Fact]
    public async Task UpsertMetadataAsync_ParentUnitOnly_IsIgnored()
    {
        // ADR-0040: ParentUnit is owned by unit_memberships and ignored
        // by the live-config repository. The patch as a whole is a no-op
        // when ParentUnit is the only non-null field.
        var ct = TestContext.Current.CancellationToken;

        var written = await _repository.UpsertMetadataAsync(
            Agent1, new AgentMetadata(ParentUnit: "engineering"), ct);

        written.ShouldBeEmpty();
    }

    [Fact]
    public async Task SetExpertiseAsync_DedupesByNameLastWriteWins()
    {
        var ct = TestContext.Current.CancellationToken;

        var input = new[]
        {
            new ExpertiseDomain("python", "scripting", ExpertiseLevel.Beginner),
            new ExpertiseDomain("python", "fastapi authoring", ExpertiseLevel.Expert),
            new ExpertiseDomain("rust", string.Empty, ExpertiseLevel.Intermediate),
        };

        var persisted = await _repository.SetExpertiseAsync(Agent1, input, ct);

        persisted.Length.ShouldBe(2);
        var python = persisted.Single(d => d.Name == "python");
        python.Description.ShouldBe("fastapi authoring");
        python.Level.ShouldBe(ExpertiseLevel.Expert);
    }

    [Fact]
    public async Task SetExpertiseAsync_ReplaceInFull_RemovesOldDomains()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.SetExpertiseAsync(Agent1, new[]
        {
            new ExpertiseDomain("python", string.Empty, ExpertiseLevel.Expert),
            new ExpertiseDomain("rust", string.Empty, ExpertiseLevel.Intermediate),
        }, ct);

        await _repository.SetExpertiseAsync(Agent1, new[]
        {
            new ExpertiseDomain("python", string.Empty, ExpertiseLevel.Expert),
        }, ct);

        var fetched = await _repository.GetExpertiseAsync(Agent1, ct);
        fetched.Length.ShouldBe(1);
        fetched[0].Name.ShouldBe("python");
    }

    [Fact]
    public async Task SetExpertiseAsync_FlipsExpertiseInitialisedFlag_EvenForEmptyList()
    {
        // The activation seeder relies on the flag — even a deliberate
        // "clear all expertise" must turn it on so the YAML seed isn't
        // re-applied at next activation.
        var ct = TestContext.Current.CancellationToken;

        (await _repository.HasExpertiseSetAsync(Agent1, ct)).ShouldBeFalse();

        await _repository.SetExpertiseAsync(Agent1, Array.Empty<ExpertiseDomain>(), ct);

        (await _repository.HasExpertiseSetAsync(Agent1, ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task ConfigSurvivesAcrossDbContextInstances()
    {
        // Cross-restart proxy: each repository instance gets its own
        // DbContext, simulating actor reactivation reading the same row.
        var ct = TestContext.Current.CancellationToken;
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        await using (var write = new SpringDbContext(options))
        {
            var writer = new AgentLiveConfigRepository(write);
            await writer.UpsertMetadataAsync(
                Agent1,
                new AgentMetadata(
                    Model: "claude-opus",
                    Enabled: true,
                    ExecutionMode: AgentExecutionMode.OnDemand),
                ct);
            await writer.SetExpertiseAsync(Agent1, new[]
            {
                new ExpertiseDomain("python", string.Empty, ExpertiseLevel.Expert),
            }, ct);
        }

        await using (var read = new SpringDbContext(options))
        {
            var reader = new AgentLiveConfigRepository(read);
            var metadata = await reader.GetMetadataAsync(Agent1, ct);
            metadata.Model.ShouldBe("claude-opus");
            metadata.Enabled.ShouldBe(true);
            metadata.ExecutionMode.ShouldBe(AgentExecutionMode.OnDemand);

            var expertise = await reader.GetExpertiseAsync(Agent1, ct);
            expertise.Length.ShouldBe(1);
            expertise[0].Name.ShouldBe("python");

            (await reader.HasExpertiseSetAsync(Agent1, ct)).ShouldBeTrue();
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
