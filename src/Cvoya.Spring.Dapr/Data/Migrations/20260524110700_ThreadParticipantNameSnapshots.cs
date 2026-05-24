// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Adds the <c>participant_name_snapshots</c> jsonb column to
/// <c>spring.threads</c> (#2533). The column stores an
/// <c>address → last-known display name</c> map captured at message-write
/// time so the engagement list still surfaces real names after the
/// underlying agent / unit / connector definition is soft-deleted —
/// avoiding the "an agent" / "a connector" fallback when we already
/// know what the human was talking to.
///
/// <para>
/// The column is non-nullable with a server-side default of <c>{}</c> so
/// existing rows behave as if they had an empty snapshot until the
/// writer fills them in on the next message arrival. No backfill — the
/// gap is acceptable for v0.1 because the previous engagement list was
/// already serving the generic fallback for soft-deleted rows.
/// </para>
/// </summary>
public partial class ThreadParticipantNameSnapshots : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "participant_name_snapshots",
            schema: "spring",
            table: "threads",
            type: "jsonb",
            nullable: false,
            defaultValue: "{}");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "participant_name_snapshots",
            schema: "spring",
            table: "threads");
    }
}
