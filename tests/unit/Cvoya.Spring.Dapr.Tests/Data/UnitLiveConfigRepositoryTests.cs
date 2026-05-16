// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitLiveConfigRepository"/> (#2049 / ADR-0040).
/// Verifies the upsert / read paths against the EF in-memory provider —
/// the integration suite exercises the same surface against Postgres.
/// These fast tests pin the contract that the actor / coordinator
/// rely on: partial PATCH metadata, empty-boundary as null jsonb,
/// inheritance default of Inherit, replace-in-full expertise writes,
/// and the cross-restart "is expertise initialised?" flag.
/// </summary>
public class UnitLiveConfigRepositoryTests : IDisposable
{
    private static readonly Guid Unit1 = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid Unit2 = new("bbbbbbbb-0000-0000-0000-000000000002");

    private readonly SpringDbContext _context;
    private readonly UnitLiveConfigRepository _repository;

    public UnitLiveConfigRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new SpringDbContext(options);
        _repository = new UnitLiveConfigRepository(_context);
    }

    [Fact]
    public async Task GetMetadataAsync_NoRow_ReturnsAllNullMetadata()
    {
        var ct = TestContext.Current.CancellationToken;

        var fetched = await _repository.GetMetadataAsync(Unit1, ct);

        fetched.DisplayName.ShouldBeNull();
        fetched.Description.ShouldBeNull();
        fetched.Model.ShouldBeNull();
        fetched.Color.ShouldBeNull();
        fetched.Provider.ShouldBeNull();
        fetched.Hosting.ShouldBeNull();
    }

    [Fact]
    public async Task UpsertMetadataAsync_AllFields_PersistsAndRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;

        var written = await _repository.UpsertMetadataAsync(
            Unit1,
            new UnitMetadata(
                DisplayName: null,
                Description: null,
                Model: "claude-opus",
                Color: "#aabbcc",
                Provider: "anthropic",
                Hosting: "ephemeral"),
            ct);

        written.Count.ShouldBe(4);

        var fetched = await _repository.GetMetadataAsync(Unit1, ct);
        fetched.Model.ShouldBe("claude-opus");
        fetched.Color.ShouldBe("#aabbcc");
        fetched.Provider.ShouldBe("anthropic");
        fetched.Hosting.ShouldBe("ephemeral");
    }

    [Fact]
    public async Task UpsertMetadataAsync_PartialPatch_LeavesUnsetFieldsAlone()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.UpsertMetadataAsync(
            Unit1,
            new UnitMetadata(null, null, "gpt-4o", "#aabbcc", "openai", "persistent"),
            ct);

        var written = await _repository.UpsertMetadataAsync(
            Unit1,
            new UnitMetadata(null, null, null, "#ddeeff"),
            ct);

        written.Count.ShouldBe(1);
        written.ShouldContain("Color");

        var fetched = await _repository.GetMetadataAsync(Unit1, ct);
        fetched.Model.ShouldBe("gpt-4o");
        fetched.Color.ShouldBe("#ddeeff");
        fetched.Provider.ShouldBe("openai");
        fetched.Hosting.ShouldBe("persistent");
    }

    [Fact]
    public async Task UpsertMetadataAsync_AllNull_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;

        var written = await _repository.UpsertMetadataAsync(
            Unit1, new UnitMetadata(null, null, null, null), ct);

        written.ShouldBeEmpty();

        var fetched = await _repository.GetMetadataAsync(Unit1, ct);
        fetched.Model.ShouldBeNull();
    }

    [Fact]
    public async Task UpsertMetadataAsync_ParityFields_PersistAndRoundTrip()
    {
        // #2341: Specialty / Enabled / ExecutionMode were added to UnitMetadata
        // for unit/agent parity. Confirm the live-config repository persists
        // them through the actor-owned write path the same way Model/Color do.
        var ct = TestContext.Current.CancellationToken;

        var written = await _repository.UpsertMetadataAsync(
            Unit1,
            new UnitMetadata(
                DisplayName: null,
                Description: null,
                Model: null,
                Color: null,
                Provider: null,
                Hosting: null,
                Specialty: "reviewer",
                Enabled: false,
                ExecutionMode: Cvoya.Spring.Core.Agents.AgentExecutionMode.OnDemand),
            ct);

        written.Count.ShouldBe(3);
        written.ShouldContain("Specialty");
        written.ShouldContain("Enabled");
        written.ShouldContain("ExecutionMode");

        var fetched = await _repository.GetMetadataAsync(Unit1, ct);
        fetched.Specialty.ShouldBe("reviewer");
        fetched.Enabled.ShouldBe(false);
        fetched.ExecutionMode.ShouldBe(Cvoya.Spring.Core.Agents.AgentExecutionMode.OnDemand);
    }

    [Fact]
    public async Task UpsertMetadataAsync_DisplayNameAndDescription_AreIgnored()
    {
        // ADR-0040: DisplayName / Description live on the directory
        // entity. The live-config repository ignores them. A patch with
        // only those fields is a no-op.
        var ct = TestContext.Current.CancellationToken;

        var written = await _repository.UpsertMetadataAsync(
            Unit1,
            new UnitMetadata("Platform Team", "Runs the ship", null, null),
            ct);

        written.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetBoundaryAsync_NoRow_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var fetched = await _repository.GetBoundaryAsync(Unit1, ct);
        fetched.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task SetBoundaryAsync_NonEmpty_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var boundary = new UnitBoundary(
            Opacities: new[] { new BoundaryOpacityRule(DomainPattern: "secret-*") });

        await _repository.SetBoundaryAsync(Unit1, boundary, ct);

        var fetched = await _repository.GetBoundaryAsync(Unit1, ct);
        fetched.IsEmpty.ShouldBeFalse();
        fetched.Opacities!.Count.ShouldBe(1);
        fetched.Opacities[0].DomainPattern.ShouldBe("secret-*");
    }

    [Fact]
    public async Task SetBoundaryAsync_Empty_PersistsAsNull_ReadsBackEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        // Seed a non-empty boundary, then write Empty and verify the
        // next read reports Empty (the column is nulled out).
        await _repository.SetBoundaryAsync(
            Unit1,
            new UnitBoundary(Opacities: new[] { new BoundaryOpacityRule(DomainPattern: "x") }),
            ct);

        await _repository.SetBoundaryAsync(Unit1, UnitBoundary.Empty, ct);

        var fetched = await _repository.GetBoundaryAsync(Unit1, ct);
        fetched.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task GetPermissionInheritanceAsync_NoRow_ReturnsInherit()
    {
        var ct = TestContext.Current.CancellationToken;
        var fetched = await _repository.GetPermissionInheritanceAsync(Unit1, ct);
        fetched.ShouldBe(UnitPermissionInheritance.Inherit);
    }

    [Fact]
    public async Task SetPermissionInheritanceAsync_RoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.SetPermissionInheritanceAsync(
            Unit1, UnitPermissionInheritance.Isolated, ct);

        var fetched = await _repository.GetPermissionInheritanceAsync(Unit1, ct);
        fetched.ShouldBe(UnitPermissionInheritance.Isolated);
    }

    [Fact]
    public async Task SetPermissionInheritanceAsync_Inherit_PersistsExplicitInherit()
    {
        // Per ADR-0040 / #2049 every set materialises the row so the
        // walk has a single SQL read regardless of the operator's
        // choice. Inherit is now an explicit row value, not the
        // absent-row default.
        var ct = TestContext.Current.CancellationToken;

        await _repository.SetPermissionInheritanceAsync(
            Unit1, UnitPermissionInheritance.Isolated, ct);
        await _repository.SetPermissionInheritanceAsync(
            Unit1, UnitPermissionInheritance.Inherit, ct);

        var fetched = await _repository.GetPermissionInheritanceAsync(Unit1, ct);
        fetched.ShouldBe(UnitPermissionInheritance.Inherit);
    }

    [Fact]
    public async Task SetOwnExpertiseAsync_DedupesByNameLastWriteWins()
    {
        var ct = TestContext.Current.CancellationToken;

        var input = new[]
        {
            new ExpertiseDomain("python", "scripting", ExpertiseLevel.Beginner),
            new ExpertiseDomain("python", "fastapi authoring", ExpertiseLevel.Expert),
            new ExpertiseDomain("rust", string.Empty, ExpertiseLevel.Intermediate),
        };

        var persisted = await _repository.SetOwnExpertiseAsync(Unit1, input, ct);

        persisted.Length.ShouldBe(2);
        var python = persisted.Single(d => d.Name == "python");
        python.Description.ShouldBe("fastapi authoring");
        python.Level.ShouldBe(ExpertiseLevel.Expert);
    }

    [Fact]
    public async Task SetOwnExpertiseAsync_ReplaceInFull_RemovesOldDomains()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.SetOwnExpertiseAsync(Unit1, new[]
        {
            new ExpertiseDomain("python", string.Empty, ExpertiseLevel.Expert),
            new ExpertiseDomain("rust", string.Empty, ExpertiseLevel.Intermediate),
        }, ct);

        await _repository.SetOwnExpertiseAsync(Unit1, new[]
        {
            new ExpertiseDomain("python", string.Empty, ExpertiseLevel.Expert),
        }, ct);

        var fetched = await _repository.GetOwnExpertiseAsync(Unit1, ct);
        fetched.Length.ShouldBe(1);
        fetched[0].Name.ShouldBe("python");
    }

    [Fact]
    public async Task SetOwnExpertiseAsync_FlipsExpertiseInitialisedFlag_EvenForEmptyList()
    {
        // The activation seeder relies on the flag — even a deliberate
        // "clear all expertise" must turn it on so the YAML seed isn't
        // re-applied at next activation.
        var ct = TestContext.Current.CancellationToken;

        (await _repository.HasOwnExpertiseSetAsync(Unit1, ct)).ShouldBeFalse();

        await _repository.SetOwnExpertiseAsync(Unit1, Array.Empty<ExpertiseDomain>(), ct);

        (await _repository.HasOwnExpertiseSetAsync(Unit1, ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task SetOwnExpertiseAsync_PerUnit_DoesNotLeakAcrossUnits()
    {
        var ct = TestContext.Current.CancellationToken;

        await _repository.SetOwnExpertiseAsync(Unit1, new[]
        {
            new ExpertiseDomain("python", string.Empty, ExpertiseLevel.Expert),
            new ExpertiseDomain("rust", string.Empty, ExpertiseLevel.Intermediate),
        }, ct);
        await _repository.SetOwnExpertiseAsync(Unit2, new[]
        {
            new ExpertiseDomain("ts", string.Empty, ExpertiseLevel.Advanced),
        }, ct);

        var a = await _repository.GetOwnExpertiseAsync(Unit1, ct);
        var b = await _repository.GetOwnExpertiseAsync(Unit2, ct);

        a.Length.ShouldBe(2);
        b.Length.ShouldBe(1);
        b[0].Name.ShouldBe("ts");
    }

    [Fact]
    public async Task ConfigSurvivesAcrossDbContextInstances()
    {
        // Cross-restart proxy: each repository instance gets its own
        // DbContext, simulating actor reactivation reading the same
        // row.
        var ct = TestContext.Current.CancellationToken;
        var dbName = Guid.NewGuid().ToString();
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        await using (var write = new SpringDbContext(options))
        {
            var writer = new UnitLiveConfigRepository(write);
            await writer.UpsertMetadataAsync(
                Unit1,
                new UnitMetadata(null, null, "claude-opus", "#aabbcc", "anthropic", "ephemeral"),
                ct);
            await writer.SetBoundaryAsync(
                Unit1,
                new UnitBoundary(Opacities: new[] { new BoundaryOpacityRule(DomainPattern: "*") }),
                ct);
            await writer.SetPermissionInheritanceAsync(
                Unit1, UnitPermissionInheritance.Isolated, ct);
            await writer.SetOwnExpertiseAsync(
                Unit1,
                new[] { new ExpertiseDomain("python", string.Empty, ExpertiseLevel.Expert) },
                ct);
        }

        await using (var read = new SpringDbContext(options))
        {
            var reader = new UnitLiveConfigRepository(read);
            var metadata = await reader.GetMetadataAsync(Unit1, ct);
            metadata.Model.ShouldBe("claude-opus");
            metadata.Color.ShouldBe("#aabbcc");
            metadata.Provider.ShouldBe("anthropic");
            metadata.Hosting.ShouldBe("ephemeral");

            var boundary = await reader.GetBoundaryAsync(Unit1, ct);
            boundary.IsEmpty.ShouldBeFalse();
            boundary.Opacities!.Count.ShouldBe(1);

            var inheritance = await reader.GetPermissionInheritanceAsync(Unit1, ct);
            inheritance.ShouldBe(UnitPermissionInheritance.Isolated);

            var expertise = await reader.GetOwnExpertiseAsync(Unit1, ct);
            expertise.Length.ShouldBe(1);
            expertise[0].Name.ShouldBe("python");

            (await reader.HasOwnExpertiseSetAsync(Unit1, ct)).ShouldBeTrue();
        }
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
