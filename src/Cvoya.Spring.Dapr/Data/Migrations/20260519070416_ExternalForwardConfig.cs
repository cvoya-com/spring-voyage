// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Issue #2503 — adds the optional <c>external_forward_config</c> jsonb
/// column to <c>tenant_activity_settings</c>. The column carries the
/// per-tenant external OpenTelemetry-backend forwarding block
/// (endpoint, protocol, headers, enabled). Null means forwarding is
/// disabled — the OSS default and the value every existing row carries
/// after the migration runs.
/// </summary>
public partial class ExternalForwardConfig : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "external_forward_config",
            schema: "spring",
            table: "tenant_activity_settings",
            type: "jsonb",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "external_forward_config",
            schema: "spring",
            table: "tenant_activity_settings");
    }
}
