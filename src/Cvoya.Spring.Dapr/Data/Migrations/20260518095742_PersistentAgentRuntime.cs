// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// Adds the <c>persistent_agent_runtime</c> table that backs
/// <c>PersistentAgentRegistry</c>'s cross-process source of truth (#2468).
/// Before this migration the registry was an in-memory
/// <c>ConcurrentDictionary</c> per host process — the worker's auto-deploy
/// path wrote to its own copy and the API's deployment / runtime-status /
/// logs endpoints read from theirs, so the portal's "Persistent deployment"
/// badge reported <c>Not deployed</c> for any agent the worker auto-deployed
/// on first inbound message. The new table is the shared backing store; the
/// in-process dictionary remains as a per-process cache.
/// </summary>
public partial class PersistentAgentRuntime : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "persistent_agent_runtime",
            schema: "spring",
            columns: table => new
            {
                agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                endpoint = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                container_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                health_status = table.Column<int>(type: "integer", nullable: false),
                consecutive_failures = table.Column<int>(type: "integer", nullable: false),
                sidecar_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                sidecar_network_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                image = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                owner_host = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_persistent_agent_runtime", x => x.agent_id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_persistent_agent_runtime_tenant_id",
            schema: "spring",
            table: "persistent_agent_runtime",
            column: "tenant_id");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "persistent_agent_runtime",
            schema: "spring");
    }
}
