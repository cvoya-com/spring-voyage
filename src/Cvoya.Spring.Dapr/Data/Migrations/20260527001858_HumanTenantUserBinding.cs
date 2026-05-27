// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// ADR-0062 — explicit <c>Human → TenantUser</c> binding.
///
/// <para>
/// Adds <c>humans.tenant_user_id</c> (NOT NULL FK to <c>tenant_users.id</c>)
/// per § 1, and <c>tenant_users.primary_human_id</c> (nullable FK to
/// <c>humans.id</c>) per § 2. The forward-only stance follows the v0.1
/// freezing-release rule (no live cloud data to migrate). The seeded
/// default-tenant rows are backfilled in code by
/// <c>DefaultTenantUserSeedProvider</c> (§ 9) so pre-existing Human rows
/// resolve to the operator <c>TenantUser</c> on the next host start.
/// </para>
/// </summary>
public partial class HumanTenantUserBinding : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "primary_human_id",
            schema: "spring",
            table: "tenant_users",
            type: "uuid",
            nullable: true);

        // NOT NULL with a Guid.Empty server-side default so the column
        // accepts existing rows at migration time; the seed provider
        // backfills empty values to OssTenantUserIds.Operator on the
        // next host start. ADR-0062 § 9.
        migrationBuilder.AddColumn<Guid>(
            name: "tenant_user_id",
            schema: "spring",
            table: "humans",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

        migrationBuilder.CreateIndex(
            name: "ix_humans_tenant_user_id",
            schema: "spring",
            table: "humans",
            column: "tenant_user_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "ix_humans_tenant_user_id",
            schema: "spring",
            table: "humans");

        migrationBuilder.DropColumn(
            name: "primary_human_id",
            schema: "spring",
            table: "tenant_users");

        migrationBuilder.DropColumn(
            name: "tenant_user_id",
            schema: "spring",
            table: "humans");
    }
}
