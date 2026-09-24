using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LLMProxy.Infrastructure.Persistence.Migrations.Postgres;

/// <inheritdoc />
public partial class ProviderKeyPools : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProviderKeyId",
            table: "upstream_attempts",
            type: "character varying(36)",
            maxLength: 36,
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ProviderKeyId",
            table: "upstream_attempts");
    }
}
