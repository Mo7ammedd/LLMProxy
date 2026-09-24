using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace LLMProxy.Infrastructure.Persistence.Migrations.Postgres;

/// <inheritdoc />
public partial class ProviderOperations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "alert_evaluation_lock",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_alert_evaluation_lock", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "operational_alerts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Fingerprint = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                Resource = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Severity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                Message = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                AcknowledgedBy = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                Occurrences = table.Column<int>(type: "integer", nullable: false),
                DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DeliveryLeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                DeliveryAttempts = table.Column<int>(type: "integer", nullable: false),
                NextDeliveryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operational_alerts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "operations_revision",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                Version = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operations_revision", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "provider_keys",
            columns: table => new
            {
                Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                KeyId = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                Label = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                Ciphertext = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_provider_keys", x => new { x.Provider, x.KeyId });
            });

        migrationBuilder.InsertData(
            table: "alert_evaluation_lock",
            column: "Id",
            value: 1);

        migrationBuilder.InsertData(
            table: "operations_revision",
            columns: new[] { "Id", "Version" },
            values: new object[] { 1, 0L });

        migrationBuilder.CreateIndex(
            name: "IX_upstream_attempts_CreatedAt_Provider_ProviderKeyId",
            table: "upstream_attempts",
            columns: new[] { "CreatedAt", "Provider", "ProviderKeyId" });

        migrationBuilder.CreateIndex(
            name: "IX_operational_alerts_Fingerprint",
            table: "operational_alerts",
            column: "Fingerprint",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_operational_alerts_ResolvedAt_StartedAt",
            table: "operational_alerts",
            columns: new[] { "ResolvedAt", "StartedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "alert_evaluation_lock");

        migrationBuilder.DropTable(
            name: "operational_alerts");

        migrationBuilder.DropTable(
            name: "operations_revision");

        migrationBuilder.DropTable(
            name: "provider_keys");

        migrationBuilder.DropIndex(
            name: "IX_upstream_attempts_CreatedAt_Provider_ProviderKeyId",
            table: "upstream_attempts");
    }
}
