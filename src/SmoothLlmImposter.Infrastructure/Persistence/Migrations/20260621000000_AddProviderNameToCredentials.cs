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
