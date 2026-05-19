// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// ADR-0047 §§ 1, 2, 8 — drops <c>human_connector_identities</c> and
/// creates the two new principal-side tables that replace it.
/// </summary>
/// <remarks>
/// <para>
/// The pre-ADR-0047 <c>HumanConnectorIdentity</c> shape conflated
/// "what is this <c>Human</c>'s GitHub login?" with the wrong principal —
/// connector identity belongs on the authenticated <c>TenantUser</c>, not
/// on every <c>Human</c> configuration row. This migration:
/// </para>
/// <list type="number">
///   <item><description>
///     Drops the <c>human_connector_identities</c> table outright. v0.1
///     is the freezing release per ADR-0036 § "Schema reset" / ADR-0046 §7
///     / the standing clean-deploy rule — no row-level migration, local
///     dev databases are reset on the v0.1 deploy.
///   </description></item>
///   <item><description>
///     Creates <c>tenant_users</c> with the natural key
///     <c>(tenant_id, auth_subject)</c> per ADR-0047 §1. The unique index
///     is partial (<c>auth_subject IS NOT NULL</c>) so OSS rows pinned by
///     the deterministic <c>OssTenantUserIds.Operator</c> UUID can exist
///     without an OAuth subject without colliding on a single all-null
///     slot.
///   </description></item>
///   <item><description>
///     Creates <c>tenant_user_connector_identities</c> with two unique
///     indices per ADR-0047 §2:
///     <c>(tenant_id, tenant_user_id, connector_id)</c> as the natural
///     key (one identity per <c>(tenant_user, connector)</c> pair) and
///     <c>(tenant_id, connector_id, username)</c> for the reverse lookup
///     "given a connector login, which tenant user claimed it?"
///   </description></item>
/// </list>
/// <para>
/// The migration is forward-only. The auto-generated <c>Down()</c>
/// recreates the prior shape so EF tooling stays happy, but the v0.1
/// clean-deploy policy means rollbacks are not a supported operational
/// path.
/// </para>
/// </remarks>
public partial class TenantUserAndConnectorIdentityRekey : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "human_connector_identities",
            schema: "spring");

        migrationBuilder.CreateTable(
            name: "tenant_user_connector_identities",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                connector_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                display_handle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_user_connector_identities", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "tenant_users",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                auth_subject = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                description = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_users", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ux_tenant_user_connector_identities_tenant_connector_username",
            schema: "spring",
            table: "tenant_user_connector_identities",
            columns: new[] { "tenant_id", "connector_id", "username" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_tenant_user_connector_identities_tenant_user_connector",
            schema: "spring",
            table: "tenant_user_connector_identities",
            columns: new[] { "tenant_id", "tenant_user_id", "connector_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_tenant_users_tenant_auth_subject",
            schema: "spring",
            table: "tenant_users",
            columns: new[] { "tenant_id", "auth_subject" },
            unique: true,
            filter: "auth_subject IS NOT NULL");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "tenant_user_connector_identities",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "tenant_users",
            schema: "spring");

        migrationBuilder.CreateTable(
            name: "human_connector_identities",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                connector_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                connector_user_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                display_handle = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                human_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_human_connector_identities", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "ix_human_connector_identities_tenant_human",
            schema: "spring",
            table: "human_connector_identities",
            columns: new[] { "tenant_id", "human_id" });

        migrationBuilder.CreateIndex(
            name: "ux_human_connector_identities_tenant_connector_user",
            schema: "spring",
            table: "human_connector_identities",
            columns: new[] { "tenant_id", "connector_id", "connector_user_id" },
            unique: true);
    }
}
