// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// ADR-0056 §7 / §9 — backfills historical rows in
/// <c>spring.activity_events</c> whose <c>event_type</c> column reads
/// <c>'MessageReceived'</c> (the legacy enum name) to the new
/// <c>'MessageArrived'</c> name. The activity event type was renamed in
/// place; the enum ordinal is preserved (actor-remoting wire format is
/// unchanged) so no schema-level rewrite is needed, only the string-typed
/// column that <see cref="Cvoya.Spring.Dapr.Data.Entities.ActivityEventEntity"/>
/// persists for portable querying.
/// </summary>
/// <remarks>
/// No deprecation alias: the activity-stream consumers (CLI, web, analytics)
/// move to the new name atomically with this migration. Historical rows must
/// render under the new name so portal queries / saved CLI filters keep
/// working without a per-deployment one-off update step.
/// </remarks>
public partial class ActivityEventTypeMessageArrivedRename : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "UPDATE spring.activity_events " +
            "SET event_type = 'MessageArrived' " +
            "WHERE event_type = 'MessageReceived';");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "UPDATE spring.activity_events " +
            "SET event_type = 'MessageReceived' " +
            "WHERE event_type = 'MessageArrived';");
    }
}
