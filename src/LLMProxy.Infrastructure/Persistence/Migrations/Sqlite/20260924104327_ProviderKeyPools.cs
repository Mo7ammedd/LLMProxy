using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LLMProxy.Infrastructure.Persistence.Migrations.Sqlite;

/// <inheritdoc />
public partial class ProviderKeyPools : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProviderKeyId",
            table: "upstream_attempts",
            type: "TEXT",
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
