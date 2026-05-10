// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using System;
using System.Text.Json;

using Microsoft.EntityFrameworkCore.Migrations;

/// <inheritdoc />
public partial class InitialBaseline : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(
            name: "spring");

        migrationBuilder.CreateTable(
            name: "activity_events",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                source_id = table.Column<Guid>(type: "uuid", nullable: false),
                event_type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                severity = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                summary = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                details = table.Column<JsonElement>(type: "jsonb", nullable: true),
                correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                cost = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_activity_events", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "agent_definitions",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                role = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                definition = table.Column<JsonElement>(type: "jsonb", nullable: true),
                created_by = table.Column<Guid>(type: "uuid", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_definitions", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "agent_expertise",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                description = table.Column<string>(type: "text", nullable: false),
                level = table.Column<int>(type: "integer", nullable: true),
                input_schema_json = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_expertise", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "agent_live_config",
            schema: "spring",
            columns: table => new
            {
                agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                specialty = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                execution_mode = table.Column<int>(type: "integer", nullable: false),
                expertise_initialised = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_live_config", x => x.agent_id);
            });

        migrationBuilder.CreateTable(
            name: "agent_skill_grants",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                skill_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_skill_grants", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "api_tokens",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: true),
                token_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                scopes = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_api_tokens", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "budget_limits",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                daily_budget = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_budget_limits", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "cloning_policies",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                scope_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                scope_id = table.Column<Guid>(type: "uuid", nullable: true),
                policy = table.Column<JsonElement>(type: "jsonb", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_cloning_policies", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "connector_definitions",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                config = table.Column<JsonElement>(type: "jsonb", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                install_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Active"),
                install_id = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_connector_definitions", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "cost_records",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_id = table.Column<Guid>(type: "uuid", nullable: true),
                model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                input_tokens = table.Column<int>(type: "integer", nullable: false),
                output_tokens = table.Column<int>(type: "integer", nullable: false),
                cost = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                duration = table.Column<TimeSpan>(type: "interval", nullable: true),
                timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                correlation_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Work")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_cost_records", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "credential_health",
            schema: "spring",
            columns: table => new
            {
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                kind = table.Column<int>(type: "integer", nullable: false),
                subject_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                secret_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                status = table.Column<int>(type: "integer", nullable: false),
                last_error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                last_checked = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_credential_health", x => new { x.tenant_id, x.kind, x.subject_id, x.secret_name });
            });

        migrationBuilder.CreateTable(
            name: "humans",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                username = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                email = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                permission_level = table.Column<int>(type: "integer", nullable: false),
                notification_preferences = table.Column<string>(type: "jsonb", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_humans", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "package_installs",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                install_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                package_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                original_manifest_yaml = table.Column<string>(type: "text", nullable: false),
                inputs_json = table.Column<string>(type: "text", nullable: false),
                package_root = table.Column<string>(type: "text", nullable: true),
                started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                error_message = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_package_installs", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "secret_registry_entries",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                scope = table.Column<int>(type: "integer", nullable: false),
                owner_id = table.Column<Guid>(type: "uuid", nullable: true),
                name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                store_key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                origin = table.Column<int>(type: "integer", nullable: false),
                version = table.Column<int>(type: "integer", nullable: true),
                propagate = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_secret_registry_entries", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "tenant_connector_installs",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                connector_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                config = table.Column<JsonElement>(type: "jsonb", nullable: true),
                installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                package_install_id = table.Column<Guid>(type: "uuid", nullable: true),
                unit_id = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_connector_installs", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "tenant_model_provider_installs",
            schema: "spring",
            columns: table => new
            {
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                provider_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                config = table.Column<JsonElement>(type: "jsonb", nullable: true),
                installed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_model_provider_installs", x => new { x.tenant_id, x.provider_id });
            });

        migrationBuilder.CreateTable(
            name: "tenant_skill_bundle_bindings",
            schema: "spring",
            columns: table => new
            {
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                bundle_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                enabled = table.Column<bool>(type: "boolean", nullable: false),
                bound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                install_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Active"),
                install_id = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant_skill_bundle_bindings", x => new { x.tenant_id, x.bundle_id });
            });

        migrationBuilder.CreateTable(
            name: "tenants",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                state = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenants", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "threads",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                participant_key = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                participants = table.Column<string>(type: "jsonb", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_threads", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "unit_connector_bindings",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                connector_type = table.Column<Guid>(type: "uuid", nullable: false),
                config = table.Column<JsonElement>(type: "jsonb", nullable: false),
                metadata = table.Column<JsonElement>(type: "jsonb", nullable: true),
                bound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unit_connector_bindings", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "unit_definitions",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                display_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                description = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                definition = table.Column<JsonElement>(type: "jsonb", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                last_validation_error_json = table.Column<string>(type: "text", nullable: true),
                last_validation_run_id = table.Column<string>(type: "text", nullable: true),
                install_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Active"),
                install_id = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unit_definitions", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "unit_expertise",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                description = table.Column<string>(type: "text", nullable: false),
                level = table.Column<int>(type: "integer", nullable: true),
                input_schema_json = table.Column<string>(type: "text", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unit_expertise", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "unit_human_permissions",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                human_id = table.Column<Guid>(type: "uuid", nullable: false),
                permission_level = table.Column<int>(type: "integer", nullable: false),
                identity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                notifications = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                granted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unit_human_permissions", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "unit_live_config",
            schema: "spring",
            columns: table => new
            {
                unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                color = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                provider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                hosting = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                permission_inheritance = table.Column<int>(type: "integer", nullable: false),
                boundary = table.Column<JsonElement>(type: "jsonb", nullable: true),
                expertise_initialised = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unit_live_config", x => x.unit_id);
            });

        migrationBuilder.CreateTable(
            name: "unit_memberships",
            schema: "spring",
            columns: table => new
            {
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                agent_id = table.Column<Guid>(type: "uuid", nullable: false),
                model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                specialty = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                enabled = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                execution_mode = table.Column<int>(type: "integer", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                is_primary = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unit_memberships", x => new { x.tenant_id, x.unit_id, x.agent_id });
            });

        migrationBuilder.CreateTable(
            name: "unit_policies",
            schema: "spring",
            columns: table => new
            {
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                unit_id = table.Column<Guid>(type: "uuid", nullable: false),
                skill = table.Column<JsonElement>(type: "jsonb", nullable: true),
                model = table.Column<JsonElement>(type: "jsonb", nullable: true),
                cost = table.Column<JsonElement>(type: "jsonb", nullable: true),
                execution_mode = table.Column<JsonElement>(type: "jsonb", nullable: true),
                initiative = table.Column<JsonElement>(type: "jsonb", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unit_policies", x => new { x.tenant_id, x.unit_id });
            });

        migrationBuilder.CreateTable(
            name: "unit_subunit_memberships",
            schema: "spring",
            columns: table => new
            {
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                parent_id = table.Column<Guid>(type: "uuid", nullable: false),
                child_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_unit_subunit_memberships", x => new { x.tenant_id, x.parent_id, x.child_id });
            });

        migrationBuilder.CreateTable(
            name: "messages",
            schema: "spring",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                thread_id = table.Column<Guid>(type: "uuid", nullable: false),
                sender_scheme = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                sender_id = table.Column<Guid>(type: "uuid", nullable: false),
                recipient_scheme = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                message_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                body = table.Column<string>(type: "text", nullable: true),
                payload = table.Column<string>(type: "jsonb", nullable: false),
                sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                retracted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_messages", x => x.id);
                table.ForeignKey(
                    name: "fk_messages_thread_id",
                    column: x => x.thread_id,
                    principalSchema: "spring",
                    principalTable: "threads",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_activity_events_correlation_id",
            schema: "spring",
            table: "activity_events",
            column: "correlation_id");

        migrationBuilder.CreateIndex(
            name: "IX_activity_events_tenant_id",
            schema: "spring",
            table: "activity_events",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "IX_activity_events_timestamp",
            schema: "spring",
            table: "activity_events",
            column: "timestamp");

        migrationBuilder.CreateIndex(
            name: "IX_agent_definitions_tenant_id",
            schema: "spring",
            table: "agent_definitions",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "ix_agent_expertise_tenant_agent",
            schema: "spring",
            table: "agent_expertise",
            columns: new[] { "tenant_id", "agent_id" });

        migrationBuilder.CreateIndex(
            name: "ux_agent_expertise_tenant_agent_name",
            schema: "spring",
            table: "agent_expertise",
            columns: new[] { "tenant_id", "agent_id", "name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_agent_live_config_tenant_id",
            schema: "spring",
            table: "agent_live_config",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "ix_agent_skill_grants_tenant_agent",
            schema: "spring",
            table: "agent_skill_grants",
            columns: new[] { "tenant_id", "agent_id" });

        migrationBuilder.CreateIndex(
            name: "ux_agent_skill_grants_tenant_agent_skill",
            schema: "spring",
            table: "agent_skill_grants",
            columns: new[] { "tenant_id", "agent_id", "skill_name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_api_tokens_tenant_id",
            schema: "spring",
            table: "api_tokens",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "IX_api_tokens_token_hash",
            schema: "spring",
            table: "api_tokens",
            column: "token_hash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_budget_limits_tenant_scope",
            schema: "spring",
            table: "budget_limits",
            columns: new[] { "tenant_id", "scope_type", "scope_id" },
            unique: true,
            filter: "scope_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_budget_limits_tenant_scope_null",
            schema: "spring",
            table: "budget_limits",
            columns: new[] { "tenant_id", "scope_type" },
            unique: true,
            filter: "scope_id IS NULL");

        migrationBuilder.CreateIndex(
            name: "ix_cloning_policies_tenant_scope",
            schema: "spring",
            table: "cloning_policies",
            columns: new[] { "tenant_id", "scope_type", "scope_id" },
            unique: true,
            filter: "scope_id IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "ix_cloning_policies_tenant_scope_null",
            schema: "spring",
            table: "cloning_policies",
            columns: new[] { "tenant_id", "scope_type" },
            unique: true,
            filter: "scope_id IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_connector_definitions_tenant_id",
            schema: "spring",
            table: "connector_definitions",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "IX_cost_records_agent_id",
            schema: "spring",
            table: "cost_records",
            column: "agent_id");

        migrationBuilder.CreateIndex(
            name: "IX_cost_records_tenant_id",
            schema: "spring",
            table: "cost_records",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "IX_cost_records_timestamp",
            schema: "spring",
            table: "cost_records",
            column: "timestamp");

        migrationBuilder.CreateIndex(
            name: "IX_cost_records_unit_id",
            schema: "spring",
            table: "cost_records",
            column: "unit_id");

        migrationBuilder.CreateIndex(
            name: "IX_credential_health_tenant_id",
            schema: "spring",
            table: "credential_health",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "IX_credential_health_tenant_id_kind",
            schema: "spring",
            table: "credential_health",
            columns: new[] { "tenant_id", "kind" });

        migrationBuilder.CreateIndex(
            name: "IX_humans_tenant_id",
            schema: "spring",
            table: "humans",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "IX_humans_tenant_id_username",
            schema: "spring",
            table: "humans",
            columns: new[] { "tenant_id", "username" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_messages_tenant_thread_sent_at",
            schema: "spring",
            table: "messages",
            columns: new[] { "tenant_id", "thread_id", "sent_at" });

        migrationBuilder.CreateIndex(
            name: "IX_messages_thread_id",
            schema: "spring",
            table: "messages",
            column: "thread_id");

        migrationBuilder.CreateIndex(
            name: "IX_package_installs_tenant_id",
            schema: "spring",
            table: "package_installs",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "IX_package_installs_tenant_id_install_id",
            schema: "spring",
            table: "package_installs",
            columns: new[] { "tenant_id", "install_id" });

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

        migrationBuilder.CreateIndex(
            name: "ix_tenant_connector_installs_pkg_scope",
            schema: "spring",
            table: "tenant_connector_installs",
            columns: new[] { "tenant_id", "connector_id", "package_install_id" },
            unique: true,
            filter: "\"package_install_id\" IS NOT NULL AND \"unit_id\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "IX_tenant_connector_installs_tenant_id",
            schema: "spring",
            table: "tenant_connector_installs",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "ix_tenant_connector_installs_tenant_slug",
            schema: "spring",
            table: "tenant_connector_installs",
            columns: new[] { "tenant_id", "connector_id" },
            unique: true,
            filter: "\"package_install_id\" IS NULL AND \"unit_id\" IS NULL");

        migrationBuilder.CreateIndex(
            name: "ix_tenant_connector_installs_unit_scope",
            schema: "spring",
            table: "tenant_connector_installs",
            columns: new[] { "tenant_id", "connector_id", "unit_id" },
            unique: true,
            filter: "\"unit_id\" IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_tenant_model_provider_installs_tenant_id",
            schema: "spring",
            table: "tenant_model_provider_installs",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "IX_tenant_skill_bundle_bindings_tenant_id",
            schema: "spring",
            table: "tenant_skill_bundle_bindings",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "ux_threads_tenant_participant_key",
            schema: "spring",
            table: "threads",
            columns: new[] { "tenant_id", "participant_key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ix_unit_connector_bindings_tenant_type",
            schema: "spring",
            table: "unit_connector_bindings",
            columns: new[] { "tenant_id", "connector_type" });

        migrationBuilder.CreateIndex(
            name: "ux_unit_connector_bindings_tenant_unit",
            schema: "spring",
            table: "unit_connector_bindings",
            columns: new[] { "tenant_id", "unit_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_unit_definitions_tenant_id",
            schema: "spring",
            table: "unit_definitions",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "ix_unit_expertise_tenant_unit",
            schema: "spring",
            table: "unit_expertise",
            columns: new[] { "tenant_id", "unit_id" });

        migrationBuilder.CreateIndex(
            name: "ux_unit_expertise_tenant_unit_name",
            schema: "spring",
            table: "unit_expertise",
            columns: new[] { "tenant_id", "unit_id", "name" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "ux_unit_human_permissions_tenant_unit_human",
            schema: "spring",
            table: "unit_human_permissions",
            columns: new[] { "tenant_id", "unit_id", "human_id" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_unit_live_config_tenant_id",
            schema: "spring",
            table: "unit_live_config",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "ix_unit_memberships_tenant_agent_id",
            schema: "spring",
            table: "unit_memberships",
            columns: new[] { "tenant_id", "agent_id" });

        migrationBuilder.CreateIndex(
            name: "ix_unit_subunit_memberships_tenant_child",
            schema: "spring",
            table: "unit_subunit_memberships",
            columns: new[] { "tenant_id", "child_id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "activity_events",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "agent_definitions",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "agent_expertise",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "agent_live_config",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "agent_skill_grants",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "api_tokens",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "budget_limits",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "cloning_policies",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "connector_definitions",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "cost_records",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "credential_health",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "humans",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "messages",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "package_installs",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "secret_registry_entries",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "tenant_connector_installs",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "tenant_model_provider_installs",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "tenant_skill_bundle_bindings",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "tenants",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "unit_connector_bindings",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "unit_definitions",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "unit_expertise",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "unit_human_permissions",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "unit_live_config",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "unit_memberships",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "unit_policies",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "unit_subunit_memberships",
            schema: "spring");

        migrationBuilder.DropTable(
            name: "threads",
            schema: "spring");
    }
}
