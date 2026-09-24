using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LLMProxy.Infrastructure.Persistence.Migrations.Postgres;

/// <inheritdoc />
public partial class GatewayExpansion : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Operation",
            table: "usage_records",
            type: "character varying(24)",
            maxLength: 24,
            nullable: false,
            defaultValue: "chat");

        migrationBuilder.AddColumn<string>(
            name: "Operation",
            table: "quota_reservations",
            type: "character varying(24)",
            maxLength: 24,
            nullable: false,
            defaultValue: "chat");

        migrationBuilder.AddColumn<string>(
            name: "QuotaPeriod",
            table: "quota_reservations",
            type: "character varying(7)",
            maxLength: 7,
            nullable: false,
            defaultValue: "");

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ExpiresAt",
            table: "api_keys",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "MonthlyBudgetUnits",
            table: "api_keys",
            type: "bigint",
            nullable: true);

        migrationBuilder.AddColumn<long>(
            name: "MonthlyTokenLimit",
            table: "api_keys",
            type: "bigint",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "audit_events",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                Actor = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Resource = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                StatusCode = table.Column<int>(type: "integer", nullable: false),
                RequestId = table.Column<Guid>(type: "uuid", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_audit_events", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "batch_jobs",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                InputFileId = table.Column<string>(type: "text", nullable: false),
                Endpoint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                MetadataJson = table.Column<string>(type: "text", nullable: false),
                OutputFileId = table.Column<string>(type: "text", nullable: false),
                ErrorFileId = table.Column<string>(type: "text", nullable: false),
                Total = table.Column<int>(type: "integer", nullable: false),
                Completed = table.Column<int>(type: "integer", nullable: false),
                Failed = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_batch_jobs", x => x.Id);
                table.ForeignKey(
                    name: "FK_batch_jobs_api_keys_ApiKeyId",
                    column: x => x.ApiKeyId,
                    principalTable: "api_keys",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "billing_reconciliations",
            columns: table => new
            {
                Reference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                ActualCost = table.Column<decimal>(type: "numeric(20,9)", precision: 20, scale: 9, nullable: false),
                Actor = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_billing_reconciliations", x => x.Reference);
            });

        migrationBuilder.CreateTable(
            name: "gateway_files",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                Filename = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Purpose = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                Content = table.Column<string>(type: "text", nullable: true),
                BatchId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                ErrorsOnly = table.Column<bool>(type: "boolean", nullable: false),
                Bytes = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_gateway_files", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "operators",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Username = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                PasswordHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                Role = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                Enabled = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operators", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "quota_windows",
            columns: table => new
            {
                Id = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                Period = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                UsedTokens = table.Column<long>(type: "bigint", nullable: false),
                ReservedTokens = table.Column<long>(type: "bigint", nullable: false),
                SpentUnits = table.Column<long>(type: "bigint", nullable: false),
                ReservedUnits = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_quota_windows", x => x.Id);
                table.ForeignKey(
                    name: "FK_quota_windows_api_keys_ApiKeyId",
                    column: x => x.ApiKeyId,
                    principalTable: "api_keys",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "retired_credentials",
            columns: table => new
            {
                KeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                ApiKeyId = table.Column<Guid>(type: "uuid", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_retired_credentials", x => x.KeyHash);
                table.ForeignKey(
                    name: "FK_retired_credentials_api_keys_ApiKeyId",
                    column: x => x.ApiKeyId,
                    principalTable: "api_keys",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "upstream_attempts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                ProviderRequestId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LatencyMs = table.Column<long>(type: "bigint", nullable: false),
                Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                HttpStatus = table.Column<int>(type: "integer", nullable: true),
                InputTokens = table.Column<long>(type: "bigint", nullable: false),
                OutputTokens = table.Column<long>(type: "bigint", nullable: false),
                CachedInputTokens = table.Column<long>(type: "bigint", nullable: false),
                CacheCreationTokens = table.Column<long>(type: "bigint", nullable: false),
                UsageEstimated = table.Column<bool>(type: "boolean", nullable: false),
                EstimatedCost = table.Column<decimal>(type: "numeric(20,9)", precision: 20, scale: 9, nullable: false),
                ActualCost = table.Column<decimal>(type: "numeric(20,9)", precision: 20, scale: 9, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_upstream_attempts", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "batch_items",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                BatchId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Index = table.Column<int>(type: "integer", nullable: false),
                CustomId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Body = table.Column<string>(type: "text", nullable: false),
                Status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                LeaseOwner = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                HttpStatus = table.Column<int>(type: "integer", nullable: true),
                ResultJson = table.Column<string>(type: "text", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_batch_items", x => x.Id);
                table.ForeignKey(
                    name: "FK_batch_items_batch_jobs_BatchId",
                    column: x => x.BatchId,
                    principalTable: "batch_jobs",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "operator_sessions",
            columns: table => new
            {
                TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                OperatorId = table.Column<Guid>(type: "uuid", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_operator_sessions", x => x.TokenHash);
                table.ForeignKey(
                    name: "FK_operator_sessions_operators_OperatorId",
                    column: x => x.OperatorId,
                    principalTable: "operators",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_usage_records_CreatedAt_RequestId",
            table: "usage_records",
            columns: new[] { "CreatedAt", "RequestId" });

        migrationBuilder.CreateIndex(
            name: "IX_audit_events_CreatedAt_Id",
            table: "audit_events",
            columns: new[] { "CreatedAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_batch_items_BatchId_Index",
            table: "batch_items",
            columns: new[] { "BatchId", "Index" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_batch_items_Status_LeaseExpiresAt",
            table: "batch_items",
            columns: new[] { "Status", "LeaseExpiresAt" });

        migrationBuilder.CreateIndex(
            name: "IX_batch_jobs_ApiKeyId_CreatedAt_Id",
            table: "batch_jobs",
            columns: new[] { "ApiKeyId", "CreatedAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_billing_reconciliations_AttemptId",
            table: "billing_reconciliations",
            column: "AttemptId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_gateway_files_ApiKeyId_CreatedAt_Id",
            table: "gateway_files",
            columns: new[] { "ApiKeyId", "CreatedAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_operator_sessions_ExpiresAt",
            table: "operator_sessions",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_operator_sessions_OperatorId",
            table: "operator_sessions",
            column: "OperatorId");

        migrationBuilder.CreateIndex(
            name: "IX_operators_Username",
            table: "operators",
            column: "Username",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_quota_windows_ApiKeyId_Period",
            table: "quota_windows",
            columns: new[] { "ApiKeyId", "Period" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_retired_credentials_ApiKeyId",
            table: "retired_credentials",
            column: "ApiKeyId");

        migrationBuilder.CreateIndex(
            name: "IX_retired_credentials_ExpiresAt",
            table: "retired_credentials",
            column: "ExpiresAt");

        migrationBuilder.CreateIndex(
            name: "IX_upstream_attempts_RequestId_CreatedAt",
            table: "upstream_attempts",
            columns: new[] { "RequestId", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "audit_events");

        migrationBuilder.DropTable(
            name: "batch_items");

        migrationBuilder.DropTable(
            name: "billing_reconciliations");

        migrationBuilder.DropTable(
            name: "gateway_files");

        migrationBuilder.DropTable(
            name: "operator_sessions");

        migrationBuilder.DropTable(
            name: "quota_windows");

        migrationBuilder.DropTable(
            name: "retired_credentials");

        migrationBuilder.DropTable(
            name: "upstream_attempts");

        migrationBuilder.DropTable(
            name: "batch_jobs");

        migrationBuilder.DropTable(
            name: "operators");

        migrationBuilder.DropIndex(
            name: "IX_usage_records_CreatedAt_RequestId",
            table: "usage_records");

        migrationBuilder.DropColumn(
            name: "Operation",
            table: "usage_records");

        migrationBuilder.DropColumn(
            name: "Operation",
            table: "quota_reservations");

        migrationBuilder.DropColumn(
            name: "QuotaPeriod",
            table: "quota_reservations");

        migrationBuilder.DropColumn(
            name: "ExpiresAt",
            table: "api_keys");

        migrationBuilder.DropColumn(
            name: "MonthlyBudgetUnits",
            table: "api_keys");

        migrationBuilder.DropColumn(
            name: "MonthlyTokenLimit",
            table: "api_keys");
    }
}
