// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Adds the <c>tenant_activity_settings</c> table that backs
/// <see cref="Cvoya.Spring.Core.Capabilities.ITenantActivitySettings"/>
/// (issue #2492). One row per tenant carries the capture level
/// (off / summary / full) and the retention horizon in days; absence
/// resolves to OSS defaults at the service layer.
/// </summary>
public partial class ActivityCaptureSettings : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "tenant_activity_settings",
            schema: "spring",
            columns: table => new
            {
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                level = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                retention_days = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_activity_settings", x => x.tenant_id);
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "tenant_activity_settings",
            schema: "spring");
    }
}
