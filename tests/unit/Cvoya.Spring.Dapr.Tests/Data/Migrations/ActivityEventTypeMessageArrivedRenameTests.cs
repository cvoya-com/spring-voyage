// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data.Migrations;

using Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the ADR-0056 §9 backfill migration emits the in-place rename
/// SQL on Up and the reverse rewrite on Down. Real DB execution is covered
/// by the deployment-time migration run; this test guards the SQL shape so
/// a careless refactor cannot drop the backfill without the failure being
/// visible in unit-test land.
/// </summary>
public class ActivityEventTypeMessageArrivedRenameTests
{
    private static IReadOnlyList<MigrationOperation> CollectOperations(System.Action<MigrationBuilder> apply)
    {
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        apply(builder);
        return builder.Operations;
    }

    [Fact]
    public void Up_BackfillsMessageReceivedRowsToMessageArrived()
    {
        var migration = new ActivityEventTypeMessageArrivedRename();

        var operations = CollectOperations(b =>
        {
            // Reflection on the protected Up method — Migration's public
            // surface is the IMigration interface, which Migration
            // implements via internal helpers; tests use the same path
            // EF uses to materialise operations.
            var method = typeof(Migration).GetMethod(
                "Up",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            method!.Invoke(migration, [b]);
        });

        var sqlOps = operations.OfType<SqlOperation>().ToList();
        sqlOps.Count.ShouldBe(1);
        sqlOps[0].Sql.ShouldContain("UPDATE spring.activity_events");
        sqlOps[0].Sql.ShouldContain("SET event_type = 'MessageArrived'");
        sqlOps[0].Sql.ShouldContain("WHERE event_type = 'MessageReceived'");
    }

    [Fact]
    public void Down_ReversesMessageArrivedRowsToMessageReceived()
    {
        var migration = new ActivityEventTypeMessageArrivedRename();

        var operations = CollectOperations(b =>
        {
            var method = typeof(Migration).GetMethod(
                "Down",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            method!.Invoke(migration, [b]);
        });

        var sqlOps = operations.OfType<SqlOperation>().ToList();
        sqlOps.Count.ShouldBe(1);
        sqlOps[0].Sql.ShouldContain("UPDATE spring.activity_events");
        sqlOps[0].Sql.ShouldContain("SET event_type = 'MessageReceived'");
        sqlOps[0].Sql.ShouldContain("WHERE event_type = 'MessageArrived'");
    }
}
