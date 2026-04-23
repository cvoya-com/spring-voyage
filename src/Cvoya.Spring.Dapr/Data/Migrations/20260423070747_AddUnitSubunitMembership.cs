// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

using System;

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <summary>
    /// Adds the <c>unit_subunit_memberships</c> table — a persistent
    /// projection of the parent → child unit edge maintained in
    /// <c>UnitActor</c> state. Lets the tenant-tree endpoint and any
    /// other reader resolve nested unit hierarchies without a per-unit
    /// actor fanout (#1154).
    /// </summary>
    public partial class AddUnitSubunitMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "unit_subunit_memberships",
                schema: "spring",
                columns: table => new
                {
                    tenant_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    parent_unit_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    child_unit_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_unit_subunit_memberships", x => new { x.tenant_id, x.parent_unit_id, x.child_unit_id });
                });

            migrationBuilder.CreateIndex(
                name: "ix_unit_subunit_memberships_tenant_child",
                schema: "spring",
                table: "unit_subunit_memberships",
                columns: new[] { "tenant_id", "child_unit_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "unit_subunit_memberships",
                schema: "spring");
        }
    }
}