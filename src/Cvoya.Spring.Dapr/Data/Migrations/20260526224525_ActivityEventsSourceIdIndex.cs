// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// #2800 — replaces the standalone <c>tenant_id</c> index on
/// <c>spring.activity_events</c> with a composite
/// <c>(tenant_id, source_id, timestamp DESC)</c> index that covers the
/// Activity tab's REST query: filter by <c>(tenant_id, source_id)</c>,
/// then order by <c>timestamp DESC</c> for the top-K page.
///
/// <para>
/// On a single-tenant local deployment the previous <c>tenant_id</c>-only
/// index matched every row, leaving Postgres to either sequentially scan
/// the table or walk the <c>timestamp</c> index in reverse while filtering
/// on the unindexed <c>source_id</c> column. With a chatty agent or unit
/// that produced thousands of events, both plans took seconds to return
/// — and the <c>CountAsync()</c> companion call had no <c>LIMIT</c>
/// shortcut at all. The composite serves filter + ordering + top-K in a
/// single index scan and produces the count from index entries alone.
/// </para>
/// <para>
/// The leading <c>tenant_id</c> column subsumes the dropped standalone
/// index for any query that filters on tenant only, so no new index is
/// needed for the existing tenant-scoped paths.
/// </para>
/// </summary>
public partial class ActivityEventsSourceIdIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_activity_events_tenant_id",
            schema: "spring",
            table: "activity_events");

        migrationBuilder.CreateIndex(
            name: "IX_activity_events_tenant_id_source_id_timestamp",
            schema: "spring",
            table: "activity_events",
            columns: new[] { "tenant_id", "source_id", "timestamp" },
            descending: new[] { false, false, true });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_activity_events_tenant_id_source_id_timestamp",
            schema: "spring",
            table: "activity_events");

        migrationBuilder.CreateIndex(
            name: "IX_activity_events_tenant_id",
            schema: "spring",
            table: "activity_events",
            column: "tenant_id");
    }
}
