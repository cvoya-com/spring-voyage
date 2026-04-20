// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.CredentialHealth;

using Cvoya.Spring.Core.CredentialHealth;
using Cvoya.Spring.Dapr.CredentialHealth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DefaultCredentialHealthStore"/>. Covers
/// the RecordAsync upsert semantics, Get / List round-trips, tenant
/// isolation, and the kind-filter on ListAsync.
/// </summary>
public class DefaultCredentialHealthStoreTests
{
    private const string TenantA = "tenant-a";
    private const string TenantB = "tenant-b";

    [Fact]
    public async Task RecordAsync_CreatesRowOnFirstCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateSut(Guid.NewGuid().ToString(), TenantA);

        var row = await store.RecordAsync(
            CredentialHealthKind.AgentRuntime,
            "claude",
            "default",
            CredentialHealthStatus.Valid,
            lastError: null,
            ct);

        row.TenantId.ShouldBe(TenantA);
        row.Kind.ShouldBe(CredentialHealthKind.AgentRuntime);
        row.SubjectId.ShouldBe("claude");
        row.SecretName.ShouldBe("default");
        row.Status.ShouldBe(CredentialHealthStatus.Valid);
        row.LastError.ShouldBeNull();
    }

    [Fact]
    public async Task RecordAsync_UpdatesExistingRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateSut(Guid.NewGuid().ToString(), TenantA);

        await store.RecordAsync(
            CredentialHealthKind.AgentRuntime, "claude", "default",
            CredentialHealthStatus.Valid, null, ct);
        var updated = await store.RecordAsync(
            CredentialHealthKind.AgentRuntime, "claude", "default",
            CredentialHealthStatus.Revoked, lastError: "403 from backend", ct);

        updated.Status.ShouldBe(CredentialHealthStatus.Revoked);
        updated.LastError.ShouldBe("403 from backend");
    }

    [Fact]
    public async Task RecordAsync_TruncatesLongErrors()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateSut(Guid.NewGuid().ToString(), TenantA);

        var longError = new string('x', 3000);
        var row = await store.RecordAsync(
            CredentialHealthKind.AgentRuntime, "claude", "default",
            CredentialHealthStatus.Invalid, longError, ct);

        row.LastError.ShouldNotBeNull();
        row.LastError!.Length.ShouldBe(2048);
    }

    [Fact]
    public async Task GetAsync_NoRow_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateSut(Guid.NewGuid().ToString(), TenantA);

        var row = await store.GetAsync(
            CredentialHealthKind.Connector, "github", "default", ct);
        row.ShouldBeNull();
    }

    [Fact]
    public async Task ListAsync_FiltersByKind()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateSut(Guid.NewGuid().ToString(), TenantA);

        await store.RecordAsync(
            CredentialHealthKind.AgentRuntime, "claude", "default",
            CredentialHealthStatus.Valid, null, ct);
        await store.RecordAsync(
            CredentialHealthKind.Connector, "github", "default",
            CredentialHealthStatus.Valid, null, ct);

        (await store.ListAsync(CredentialHealthKind.AgentRuntime, ct)).Count.ShouldBe(1);
        (await store.ListAsync(CredentialHealthKind.Connector, ct)).Count.ShouldBe(1);
        (await store.ListAsync(null, ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task RecordAsync_HonoursTenantIsolation()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbName = Guid.NewGuid().ToString();
        var storeA = CreateSut(dbName, TenantA);
        var storeB = CreateSut(dbName, TenantB);

        await storeA.RecordAsync(
            CredentialHealthKind.AgentRuntime, "claude", "default",
            CredentialHealthStatus.Valid, null, ct);
        await storeB.RecordAsync(
            CredentialHealthKind.AgentRuntime, "claude", "default",
            CredentialHealthStatus.Invalid, null, ct);

        (await storeA.GetAsync(
            CredentialHealthKind.AgentRuntime, "claude", "default", ct))!
            .Status.ShouldBe(CredentialHealthStatus.Valid);
        (await storeB.GetAsync(
            CredentialHealthKind.AgentRuntime, "claude", "default", ct))!
            .Status.ShouldBe(CredentialHealthStatus.Invalid);
    }

    private static DefaultCredentialHealthStore CreateSut(string dbName, string tenantId)
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;
        var context = new SpringDbContext(options, new StaticTenantContext(tenantId));
        return new DefaultCredentialHealthStore(
            context,
            new StaticTenantContext(tenantId),
            NullLogger<DefaultCredentialHealthStore>.Instance);
    }
}