// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

#nullable disable

namespace Cvoya.Spring.Dapr.Data.Migrations;

using Microsoft.EntityFrameworkCore.Migrations;

/// <summary>
/// ADR-0067 decision 3 (#3112) — retire the captured package blob.
/// <c>package_installs</c> keeps slim install provenance only (install id,
/// package name, status, timestamps, error). The three captured columns are
/// dropped because nothing replays them anymore:
/// <list type="bullet">
///   <item><description><c>original_manifest_yaml</c> — the verbatim package
///   YAML. Export reconstructs from the live relational config (#3090), not
///   from this snapshot; it was only still read by the failed-install resume
///   path, which is removed in this change.</description></item>
///   <item><description><c>inputs_json</c> — the resolved input values. Only
///   the resume / status re-parse read them; connector-secret bindings live
///   in <c>tenant_connector_installs</c>, so no reader survives.</description></item>
///   <item><description><c>package_root</c> — the on-disk root the resume path
///   re-resolved artefact files from. With resume gone it has no
///   reader.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Destructive. The reader-removal (resume path + status re-parse) lands in
/// the same change and strictly before this migration in the chain, so no
/// code reads the columns when they drop. <c>Down</c> re-adds the columns
/// (empty) for dev convenience only — the captured content is not recoverable
/// and is not needed (export reads the live config).
/// </remarks>
public partial class RetirePackageInstallBlob : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "inputs_json",
            schema: "spring",
            table: "package_installs");

        migrationBuilder.DropColumn(
            name: "original_manifest_yaml",
            schema: "spring",
            table: "package_installs");

        migrationBuilder.DropColumn(
            name: "package_root",
            schema: "spring",
            table: "package_installs");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "inputs_json",
            schema: "spring",
            table: "package_installs",
            type: "text",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "original_manifest_yaml",
            schema: "spring",
            table: "package_installs",
            type: "text",
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<string>(
            name: "package_root",
            schema: "spring",
            table: "package_installs",
            type: "text",
            nullable: true);
    }
}
