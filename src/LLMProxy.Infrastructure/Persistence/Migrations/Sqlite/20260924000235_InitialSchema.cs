using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LLMProxy.Infrastructure.Persistence.Migrations.Sqlite;

/// <inheritdoc />
public partial class InitialSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "api_keys",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                KeyHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                KeyPrefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Owner = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                AllowedModels = table.Column<string>(type: "TEXT", nullable: false),
                RequestsPerMinute = table.Column<int>(type: "INTEGER", nullable: false),
                TokenLimit = table.Column<long>(type: "INTEGER", nullable: true),
                BudgetUnits = table.Column<long>(type: "INTEGER", nullable: true),
                UsedTokens = table.Column<long>(type: "INTEGER", nullable: false),
                ReservedTokens = table.Column<long>(type: "INTEGER", nullable: false),
                SpentUnits = table.Column<long>(type: "INTEGER", nullable: false),
                ReservedUnits = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                LastUsedAt = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_api_keys", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "quota_reservations",
            columns: table => new
            {
                RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                ApiKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Tokens = table.Column<long>(type: "INTEGER", nullable: false),
                CostUnits = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_quota_reservations", x => x.RequestId);
                table.ForeignKey(
                    name: "FK_quota_reservations_api_keys_ApiKeyId",
                    column: x => x.ApiKeyId,
                    principalTable: "api_keys",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "usage_records",
            columns: table => new
            {
                RequestId = table.Column<Guid>(type: "TEXT", nullable: false),
                ApiKeyId = table.Column<Guid>(type: "TEXT", nullable: false),
                Model = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                Provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                TotalTokens = table.Column<long>(type: "INTEGER", nullable: false),
                LatencyMs = table.Column<long>(type: "INTEGER", nullable: false),
                Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                ErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                EstimatedCost = table.Column<decimal>(type: "TEXT", precision: 20, scale: 9, nullable: false),
                UsageEstimated = table.Column<bool>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_usage_records", x => x.RequestId);
                table.ForeignKey(
                    name: "FK_usage_records_api_keys_ApiKeyId",
                    column: x => x.ApiKeyId,
                    principalTable: "api_keys",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_api_keys_KeyHash",
            table: "api_keys",
            column: "KeyHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_api_keys_Owner",
            table: "api_keys",
            column: "Owner");

        migrationBuilder.CreateIndex(
            name: "IX_quota_reservations_ApiKeyId",
            table: "quota_reservations",
            column: "ApiKeyId");

        migrationBuilder.CreateIndex(
            name: "IX_quota_reservations_ExpiresAt",
            table: "quota_reservations",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_usage_records_ApiKeyId_CreatedAt",
            table: "usage_records",
            columns: new[] { "ApiKeyId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_usage_records_CreatedAt",
            table: "usage_records",
            column: "CreatedAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "quota_reservations");

        migrationBuilder.DropTable(
            name: "usage_records");

        migrationBuilder.DropTable(
            name: "api_keys");
    }
}
