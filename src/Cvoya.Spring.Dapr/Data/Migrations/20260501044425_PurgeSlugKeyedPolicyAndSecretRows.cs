// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations
{
    /// <summary>
    /// Data migration for #1488: purge rows that were stored with slug-based
    /// identity keys before the fix. After the fix:
    ///
    /// <list type="bullet">
    ///   <item><description>
    ///     <c>unit_policies.unit_id</c> stores the unit's stable ActorId (UUID),
    ///     not the human-readable slug. Existing rows stored a slug, making them
    ///     permanently unreachable from the actor layer (which reads by UUID) and
    ///     from the API (which now also reads by UUID). Truncating the table is safe;
    ///     the rows were never visible in practice.
    ///   </description></item>
    ///   <item><description>
    ///     <c>secret_registry_entries</c> rows with <c>scope = 'Unit'</c> stored
    ///     the unit slug as <c>owner_id</c>. After the fix, <c>owner_id</c> is the
    ///     unit's ActorId (UUID). Old rows are inaccessible via the new lookup path.
    ///     Purging only Unit-scoped rows; Tenant and Platform rows are keyed
    ///     correctly and must not be touched.
    ///   </description></item>
    /// </list>
    ///
    /// The Down migration is a deliberate no-op: the purged rows were permanently
    /// unreachable under the old code and restoring them would not help.
    /// </summary>
    public partial class PurgeSlugKeyedPolicyAndSecretRows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Purge all unit_policies rows. These were keyed by slug, making them
            // permanently unreachable now that the application uses UUID keys.
            // The table is small and the rows were broken, so a full truncate is safe.
            migrationBuilder.Sql("TRUNCATE spring.unit_policies;");

            // Purge Unit-scoped secret_registry_entries. The owner_id column stored
            // the unit slug; after the fix it stores the unit ActorId (UUID).
            // Unit-scoped rows stored with a slug are permanently unreachable.
            // Tenant- and Platform-scoped rows are unaffected.
            migrationBuilder.Sql("DELETE FROM spring.secret_registry_entries WHERE scope = 0;");
            // Note: 'scope = 0' corresponds to SecretScope.Unit = 0. The enum is persisted
            // as an integer via EF Core's default numeric conversion.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentional no-op: the purged rows were permanently unreachable under
            // the old code. Rolling back would not restore useful data.
        }
    }
}