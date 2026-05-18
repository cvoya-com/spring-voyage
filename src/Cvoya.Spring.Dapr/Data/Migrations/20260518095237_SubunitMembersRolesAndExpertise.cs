// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Issue #2463 — extend ADR-0046 §8's per-membership multi-valued
/// <c>roles</c> + <c>expertise</c> jsonb columns from the agent-edge
/// (<c>unit_memberships</c>) table to the sub-unit-edge
/// (<c>unit_subunit_memberships</c>) table so sub-unit members carry
/// the same per-membership metadata as agent members. Empty-array
/// defaults mean every existing row lands a valid jsonb literal without
/// a separate data-migration pass.
/// </summary>
public partial class SubunitMembersRolesAndExpertise : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "roles",
            schema: "spring",
            table: "unit_subunit_memberships",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'[]'::jsonb");

        migrationBuilder.AddColumn<string>(
            name: "expertise",
            schema: "spring",
            table: "unit_subunit_memberships",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'[]'::jsonb");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "expertise",
            schema: "spring",
            table: "unit_subunit_memberships");

        migrationBuilder.DropColumn(
            name: "roles",
            schema: "spring",
            table: "unit_subunit_memberships");
    }
}
