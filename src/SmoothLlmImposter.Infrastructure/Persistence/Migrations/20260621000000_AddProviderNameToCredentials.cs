using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SmoothLlmImposter.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
[ExcludeFromCodeCoverage]
public partial class AddProviderNameToCredentials : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProviderName",
            table: "ProviderCredentials",
            type: "character varying(128)",
            maxLength: 128,
            nullable: false,
            defaultValue: "");

        // Backfill pre-existing (dialect-keyed, HLD 002) rows to the shipped default provider keys
        // (openai-default / anthropic-default). A migration cannot read DI/config, so this is a best-effort
        // guess: lookups now key by the *configured* provider dictionary key (ProviderRoute.CredentialProviderName).
        // Operators who renamed their default provider key (HLD 007 allows arbitrary keys, e.g. "openai-official")
        // must re-key these rows to match, or the migrated credential will not resolve. See
        // .docs/wiki/setups/credentials.admin-smooth-llm-imposter.md → "Optional PostgreSQL persistence".
        // Rolling-deploy caveat: an old (pre-migration) app instance inserting during the window writes
        // ProviderName='' (the column default); the new unique index does not collide with the backfilled
        // rows, and the new app's GetActiveAsync(providerName) never matches '' — so such rows become
        // un-discoverable. Quiesce writers across this migration, or re-key '' rows afterwards.
        migrationBuilder.Sql("""
            UPDATE "ProviderCredentials"
            SET "ProviderName" = CASE "Dialect"
                WHEN 'openai' THEN 'openai-default'
                WHEN 'anthropic' THEN 'anthropic-default'
                ELSE "Dialect"
            END
            WHERE "ProviderName" = ''
            """);

        migrationBuilder.DropIndex(
            name: "IX_ProviderCredentials_Dialect_Name",
            table: "ProviderCredentials");

        migrationBuilder.CreateIndex(
            name: "IX_ProviderCredentials_Dialect_ProviderName_Name",
            table: "ProviderCredentials",
            columns: new[] { "Dialect", "ProviderName", "Name" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Additive rollback: dropping ProviderName orphans any credential created after this migration
        // (its provider scoping is lost) and reverts to the dialect-keyed uniqueness rule. Only roll back
        // before such rows exist, or export them first.
        //
        // Guard: under the new model two credentials may legally share (Dialect, Name) when ProviderName
        // differs. Recreating the dialect-keyed unique index below would then fail on a cryptic unique
        // violation. Detect that up front and fail with an actionable message instead.
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM "ProviderCredentials"
                    GROUP BY "Dialect", "Name" HAVING COUNT(*) > 1
                ) THEN
                    RAISE EXCEPTION 'Cannot roll back AddProviderNameToCredentials: rows share (Dialect, Name) but differ by ProviderName. Export or merge them before downgrading.';
                END IF;
            END $$;
            """);

        migrationBuilder.DropIndex(
            name: "IX_ProviderCredentials_Dialect_ProviderName_Name",
            table: "ProviderCredentials");

        migrationBuilder.DropColumn(
            name: "ProviderName",
            table: "ProviderCredentials");

        migrationBuilder.CreateIndex(
            name: "IX_ProviderCredentials_Dialect_Name",
            table: "ProviderCredentials",
            columns: new[] { "Dialect", "Name" },
            unique: true);
    }
}
