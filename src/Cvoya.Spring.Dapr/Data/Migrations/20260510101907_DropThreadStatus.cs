// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Drops the legacy <c>status</c> column from the <c>threads</c> table per
/// ADR-0030 / #2074. A thread is the unique, persistent, system-level record
/// for a participant set with no thread-level lifecycle state — the only
/// state machine in the model is per-(thread, participant). The column was
/// a vestige of the pre-#1268 chat-container metaphor that ADR-0030
/// supersedes.
/// </summary>
public partial class DropThreadStatus : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "status",
            schema: "spring",
            table: "threads");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "status",
            schema: "spring",
            table: "threads",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            defaultValue: "");
    }
}
