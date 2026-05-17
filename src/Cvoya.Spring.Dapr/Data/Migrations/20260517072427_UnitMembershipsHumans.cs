// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Adds the <c>unit_memberships_humans</c> table introduced in ADR-0044
/// for package-declared human team members. Sibling to
/// <c>unit_human_permissions</c> (which captures platform ACL grants);
/// this table captures domain team-role declarations from the package
/// YAML's <c>humans:</c> block.
/// </summary>
/// <remarks>
/// One row per <c>(unit, human, role)</c> triple. Uniqueness is enforced
/// by the index <c>ux_unit_memberships_humans_tenant_unit_human_role</c>;
/// the secondary index on <c>(tenant_id, unit_id, human_id)</c> backs the
/// "list my roles on this unit" read pattern. Set-semantic install
/// resolution (ADR-0044 § 3) relies on the unique index to collapse
/// duplicate declarations idempotently.
/// </remarks>
public partial class UnitMembershipsHumans : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "unit_memberships_humans",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                human_id = table.Column<Guid>(type: "uuid", nullable: false),
                role = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                expertise = table.Column<string>(type: "jsonb", nullable: false),
                notifications = table.Column<string>(type: "jsonb", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unit_memberships_humans", x => x.id);
            });

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

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "unit_memberships_humans",
            schema: "spring");
    }
}
