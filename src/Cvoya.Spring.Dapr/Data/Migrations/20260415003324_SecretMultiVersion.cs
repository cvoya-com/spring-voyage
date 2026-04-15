// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <summary>
    /// Wave 7 A5: multi-version coexistence for secrets. Replaces the
    /// 4-tuple unique index <c>(tenant, scope, owner, name)</c> with a
    /// 5-tuple unique index that includes <c>version</c>, allowing
    /// multiple rows per named secret (one per retained version). The
    /// original 4-tuple becomes a non-unique index that still supports
    /// "fetch the chain" queries.
    ///
    /// <para>
    /// Because the platform has no public deployment yet, no data
    /// migration is performed: existing rows keep their current
    /// <c>version</c> values (<c>null</c> for legacy rows inserted
    /// before the <c>AddSecretVersion</c> migration, <c>1</c> for rows
    /// inserted after). The 5-tuple index tolerates null versions
    /// under Postgres NULL semantics so legacy chains remain unique on
    /// the 4-tuple until their next rotate bumps them to an integer
    /// version.
    /// </para>
    /// </summary>
    public partial class SecretMultiVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_secret_registry_tenant_scope_owner_name",
                schema: "spring",
                table: "secret_registry_entries");

            migrationBuilder.CreateIndex(
                name: "ix_secret_registry_tenant_scope_owner_name",
                schema: "spring",
                table: "secret_registry_entries",
                columns: new[] { "tenant_id", "scope", "owner_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_secret_registry_tenant_scope_owner_name_version",
                schema: "spring",
                table: "secret_registry_entries",
                columns: new[] { "tenant_id", "scope", "owner_id", "name", "version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_secret_registry_tenant_scope_owner_name",
                schema: "spring",
                table: "secret_registry_entries");

            migrationBuilder.DropIndex(
                name: "ix_secret_registry_tenant_scope_owner_name_version",
                schema: "spring",
                table: "secret_registry_entries");

            migrationBuilder.CreateIndex(
                name: "ix_secret_registry_tenant_scope_owner_name",
                schema: "spring",
                table: "secret_registry_entries",
                columns: new[] { "tenant_id", "scope", "owner_id", "name" },
                unique: true);
        }
    }
}