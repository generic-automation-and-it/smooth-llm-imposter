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
