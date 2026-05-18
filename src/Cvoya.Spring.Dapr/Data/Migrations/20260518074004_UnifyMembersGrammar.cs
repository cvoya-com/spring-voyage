// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// ADR-0046 — unified members grammar; humans as a member kind;
/// HumanTemplate; vocabulary trim. Schema changes bundled into one
/// migration:
/// <list type="number">
///   <item><description>Drop the <c>(tenant, unit, human, role)</c> unique index on <c>unit_memberships_humans</c>.</description></item>
///   <item><description>Drop the duplicate non-unique <c>(tenant, unit, human)</c> index that ADR-0044 added (now elevated to the unique key).</description></item>
///   <item><description>Add the <c>roles jsonb</c> column on <c>unit_memberships_humans</c> with a data-migration step that lifts every existing <c>role text</c> value into a one-element JSON array.</description></item>
///   <item><description>Drop the now-superseded <c>role text</c> column.</description></item>
///   <item><description>Add the new unique <c>(tenant, unit, human)</c> index on <c>unit_memberships_humans</c>.</description></item>
///   <item><description>Add <c>roles jsonb</c> + <c>expertise jsonb</c> columns on <c>unit_memberships</c> (ADR-0046 §8) with empty-array defaults.</description></item>
///   <item><description>Add the editable <c>description text</c> column on <c>humans</c> (ADR-0046 §7 — surfaces on the Human × Config tab).</description></item>
/// </list>
/// </summary>
public partial class UnifyMembersGrammar : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Drop the ADR-0044 unique index keyed on (tenant, unit, human, role).
        // The natural key collapses to (tenant, unit, human) under ADR-0046 §7.
        migrationBuilder.DropIndex(
            name: "ux_unit_memberships_humans_tenant_unit_human_role",
            schema: "spring",
            table: "unit_memberships_humans");

        // Drop the ADR-0044 secondary non-unique index on the same triple —
        // the new unique index below replaces it.
        migrationBuilder.DropIndex(
            name: "ix_unit_memberships_humans_tenant_unit_human",
            schema: "spring",
            table: "unit_memberships_humans");

        // Add the jsonb `roles` column nullable so the data-migration UPDATE
        // can populate it from the existing per-row `role` value before we
        // flip it to NOT NULL and drop the old column.
        migrationBuilder.AddColumn<string>(
            name: "roles",
            schema: "spring",
            table: "unit_memberships_humans",
            type: "jsonb",
            nullable: true);

        // Data-migration step: lift every existing `role text` into a
        // one-element JSON array under the new `roles` column so no row
        // loses its team-role data on the column drop below.
        migrationBuilder.Sql(
            "UPDATE spring.unit_memberships_humans " +
            "SET roles = jsonb_build_array(role) " +
            "WHERE roles IS NULL;");

        // Make the new column NOT NULL (every row now has a value).
        migrationBuilder.AlterColumn<string>(
            name: "roles",
            schema: "spring",
            table: "unit_memberships_humans",
            type: "jsonb",
            nullable: false,
            oldClrType: typeof(string),
            oldType: "jsonb",
            oldNullable: true);

        migrationBuilder.DropColumn(
            name: "role",
            schema: "spring",
            table: "unit_memberships_humans");

        // ADR-0046 §8: per-membership multi-valued metadata on the agent
        // edge table. Empty-array defaults so every existing row lands a
        // valid jsonb literal without further data migration.
        migrationBuilder.AddColumn<string>(
            name: "roles",
            schema: "spring",
            table: "unit_memberships",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'[]'::jsonb");

        migrationBuilder.AddColumn<string>(
            name: "expertise",
            schema: "spring",
            table: "unit_memberships",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'[]'::jsonb");

        // ADR-0046 §7: humans gain a post-install editable Description
        // parallel to agents / units. Nullable — existing rows land NULL.
        migrationBuilder.AddColumn<string>(
            name: "description",
            schema: "spring",
            table: "humans",
            type: "text",
            nullable: true);

        // The new unique key under ADR-0046 §7 — one row per (unit, human).
        migrationBuilder.CreateIndex(
            name: "ux_unit_memberships_humans_tenant_unit_human",
            schema: "spring",
            table: "unit_memberships_humans",
            columns: new[] { "tenant_id", "unit_id", "human_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Drop the unique index introduced in Up().
        migrationBuilder.DropIndex(
            name: "ux_unit_memberships_humans_tenant_unit_human",
            schema: "spring",
            table: "unit_memberships_humans");

        // Drop the new columns first so the AddColumn / data-restore below
        // doesn't trip on lingering values.
        migrationBuilder.DropColumn(
            name: "description",
            schema: "spring",
            table: "humans");

        migrationBuilder.DropColumn(
            name: "expertise",
            schema: "spring",
            table: "unit_memberships");

        migrationBuilder.DropColumn(
            name: "roles",
            schema: "spring",
            table: "unit_memberships");

        // Restore the ADR-0044 `role text` column. Lift the first element
        // of the jsonb `roles` array back into it so rolling back doesn't
        // lose data (lossy on rows that had more than one role — same
        // caveat the v0.1 "no back-compat" policy makes explicit; this
        // path runs only when an operator manually rolls the migration
        // back, and pre-v0.1 databases never carried multi-role data).
        migrationBuilder.AddColumn<string>(
            name: "role",
            schema: "spring",
            table: "unit_memberships_humans",
            type: "character varying(128)",
            maxLength: 128,
            nullable: false,
            defaultValue: "");

        migrationBuilder.Sql(
            "UPDATE spring.unit_memberships_humans " +
            "SET role = COALESCE(roles ->> 0, '');");

        migrationBuilder.DropColumn(
            name: "roles",
            schema: "spring",
            table: "unit_memberships_humans");

        // Restore the ADR-0044 indices.
        migrationBuilder.CreateIndex(
            name: "ix_unit_memberships_humans_tenant_unit_human",
            schema: "spring",
            table: "unit_memberships_humans",
            columns: new[] { "tenant_id", "unit_id", "human_id" });

        migrationBuilder.CreateIndex(
            name: "ux_unit_memberships_humans_tenant_unit_human_role",
            schema: "spring",
            table: "unit_memberships_humans",
            columns: new[] { "tenant_id", "unit_id", "human_id", "role" },
            unique: true);
    }
}
