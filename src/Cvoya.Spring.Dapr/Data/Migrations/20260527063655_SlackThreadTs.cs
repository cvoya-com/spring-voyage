// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// ADR-0061 §3 / #2818 — Slack thread-state table.
///
/// <para>
/// Persists the SV-thread ↔ Slack-thread mapping. One row per
/// <c>(tenant, sv_thread, bound_tenant_user, team_id)</c> carrying the
/// Slack <c>thread_ts</c> of the parent message the bot posted in the
/// bound user's DM. Backs outbound delivery (post-as-thread-reply on
/// subsequent SV-side messages) and inbound routing (reverse-lookup
/// from <c>thread_ts</c> to the SV thread the reply belongs on).
/// </para>
/// </summary>
public partial class SlackThreadTs : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "slack_thread_ts",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                sv_thread_id = table.Column<Guid>(type: "uuid", nullable: false),
                bound_tenant_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                team_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                slack_thread_ts = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                slack_channel_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_slack_thread_ts", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ux_slack_thread_ts_inbound",
            schema: "spring",
            table: "slack_thread_ts",
            columns: new[] { "tenant_id", "team_id", "slack_thread_ts" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_slack_thread_ts_outbound",
            schema: "spring",
            table: "slack_thread_ts",
            columns: new[] { "tenant_id", "sv_thread_id", "bound_tenant_user_id", "team_id" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "slack_thread_ts",
            schema: "spring");
    }
}
