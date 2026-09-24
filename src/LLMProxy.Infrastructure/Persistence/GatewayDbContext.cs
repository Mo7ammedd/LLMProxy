using System.Text.Json;
using LLMProxy.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;

namespace LLMProxy.Infrastructure.Persistence;

public abstract class GatewayDbContext(DbContextOptions options) : DbContext(options)
{
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<UsageRecord> Usage => Set<UsageRecord>();
    public DbSet<QuotaReservation> Reservations => Set<QuotaReservation>();
    public DbSet<QuotaWindow> QuotaWindows => Set<QuotaWindow>();
    public DbSet<RetiredKeyCredential> RetiredCredentials => Set<RetiredKeyCredential>();
    public DbSet<OperatorAccount> Operators => Set<OperatorAccount>();
    public DbSet<OperatorSession> Sessions => Set<OperatorSession>();
    public DbSet<AuditRecord> Audit => Set<AuditRecord>();
    public DbSet<UpstreamAttempt> Attempts => Set<UpstreamAttempt>();
    public DbSet<ReconciliationRecord> Reconciliations => Set<ReconciliationRecord>();
    public DbSet<GatewayFile> Files => Set<GatewayFile>();
    public DbSet<BatchJob> Batches => Set<BatchJob>();
    public DbSet<BatchItem> BatchItems => Set<BatchItem>();
    public DbSet<StoredProviderKey> ProviderKeys => Set<StoredProviderKey>();
    public DbSet<OperationsRevision> OperationsRevisions => Set<OperationsRevision>();
    public DbSet<OperationalAlert> Alerts => Set<OperationalAlert>();
    public DbSet<AlertEvaluationLock> AlertLocks => Set<AlertEvaluationLock>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        var providerKeys = builder.Entity<StoredProviderKey>();
        providerKeys.ToTable("provider_keys").HasKey(x => new { x.Provider, x.KeyId });
        providerKeys.Property(x => x.Provider).HasMaxLength(64);
        providerKeys.Property(x => x.KeyId).HasMaxLength(36);
        providerKeys.Property(x => x.Label).HasMaxLength(128);
        providerKeys.Property(x => x.Ciphertext).HasMaxLength(12000);
        var revision = builder.Entity<OperationsRevision>();
        revision.ToTable("operations_revision").HasKey(x => x.Id);
        revision.HasData(new OperationsRevision { Id = 1, Version = 0 });
        var alerts = builder.Entity<OperationalAlert>();
        alerts.ToTable("operational_alerts").HasKey(x => x.Id);
        alerts.Property(x => x.Fingerprint).HasMaxLength(200);
        alerts.HasIndex(x => x.Fingerprint).IsUnique();
        alerts.Property(x => x.Kind).HasMaxLength(32);
        alerts.Property(x => x.Resource).HasMaxLength(128);
        alerts.Property(x => x.Severity).HasMaxLength(16);
        alerts.Property(x => x.Message).HasMaxLength(512);
        alerts.Property(x => x.AcknowledgedBy).HasMaxLength(160);
        alerts.HasIndex(x => new { x.ResolvedAt, x.StartedAt });
        var alertLock = builder.Entity<AlertEvaluationLock>();
        alertLock.ToTable("alert_evaluation_lock").HasKey(x => x.Id);
        alertLock.HasData(new AlertEvaluationLock { Id = 1 });
        var keys = builder.Entity<ApiKey>();
        keys.ToTable("api_keys");
        keys.HasKey(x => x.Id);
        keys.Property(x => x.KeyHash).HasMaxLength(64).IsRequired();
        keys.HasIndex(x => x.KeyHash).IsUnique();
        keys.Property(x => x.KeyPrefix).HasMaxLength(16).IsRequired();
        keys.Property(x => x.Owner).HasMaxLength(128).IsRequired();
        keys.HasIndex(x => x.Owner);
        keys.Property(x => x.AllowedModels).HasConversion(
            value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
            value => JsonSerializer.Deserialize<string[]>(value, (JsonSerializerOptions?)null)!)
            .Metadata.SetValueComparer(new ValueComparer<string[]>(
                (a, b) => a!.SequenceEqual(b!),
                value => value.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                value => value.ToArray()));

        var usage = builder.Entity<UsageRecord>();
        usage.ToTable("usage_records");
        usage.HasKey(x => x.RequestId);
        usage.HasOne<ApiKey>().WithMany().HasForeignKey(x => x.ApiKeyId).OnDelete(DeleteBehavior.Restrict);
        usage.Property(x => x.Model).HasMaxLength(128);
        usage.Property(x => x.Provider).HasMaxLength(64);
        usage.Property(x => x.Status).HasMaxLength(24);
        usage.Property(x => x.ErrorCode).HasMaxLength(64);
        usage.Property(x => x.EstimatedCost).HasPrecision(20, 9);
        usage.HasIndex(x => new { x.ApiKeyId, x.CreatedAt });
        usage.HasIndex(x => x.CreatedAt);
        usage.Ignore(x => x.Attempts);
        usage.Property(x => x.Operation).HasMaxLength(24);
        usage.HasIndex(x => new { x.CreatedAt, x.RequestId });

        var reservations = builder.Entity<QuotaReservation>();
        reservations.ToTable("quota_reservations");
        reservations.HasKey(x => x.RequestId);
        reservations.HasOne<ApiKey>().WithMany().HasForeignKey(x => x.ApiKeyId).OnDelete(DeleteBehavior.Restrict);
        reservations.Property(x => x.Model).HasMaxLength(128);
        reservations.HasIndex(x => x.ExpiresAt);
        reservations.Property(x => x.QuotaPeriod).HasMaxLength(7);
        reservations.Property(x => x.Operation).HasMaxLength(24);

        var windows = builder.Entity<QuotaWindow>();
        windows.ToTable("quota_windows").HasKey(x => x.Id);
        windows.Property(x => x.Id).HasMaxLength(40);
        windows.Property(x => x.Period).HasMaxLength(7);
        windows.HasIndex(x => new { x.ApiKeyId, x.Period }).IsUnique();
        windows.HasOne<ApiKey>().WithMany().HasForeignKey(x => x.ApiKeyId).OnDelete(DeleteBehavior.Restrict);
        var credentials = builder.Entity<RetiredKeyCredential>();
        credentials.ToTable("retired_credentials").HasKey(x => x.KeyHash);
        credentials.Property(x => x.KeyHash).HasMaxLength(64);
        credentials.HasIndex(x => x.ExpiresAt);
        credentials.HasOne<ApiKey>().WithMany().HasForeignKey(x => x.ApiKeyId).OnDelete(DeleteBehavior.Cascade);

        var operators = builder.Entity<OperatorAccount>();
        operators.ToTable("operators").HasKey(x => x.Id);
        operators.Property(x => x.Username).HasMaxLength(128);
        operators.HasIndex(x => x.Username).IsUnique();
        operators.Property(x => x.PasswordHash).HasMaxLength(256);
        operators.Property(x => x.Role).HasMaxLength(24);
        var sessions = builder.Entity<OperatorSession>();
        sessions.ToTable("operator_sessions").HasKey(x => x.TokenHash);
        sessions.Property(x => x.TokenHash).HasMaxLength(64);
        sessions.HasIndex(x => x.ExpiresAt);
        sessions.HasOne<OperatorAccount>().WithMany().HasForeignKey(x => x.OperatorId).OnDelete(DeleteBehavior.Cascade);
        var audit = builder.Entity<AuditRecord>();
        audit.ToTable("audit_events").HasKey(x => x.Id);
        audit.Property(x => x.Actor).HasMaxLength(160);
        audit.Property(x => x.Action).HasMaxLength(64);
        audit.Property(x => x.Resource).HasMaxLength(256);
        audit.HasIndex(x => new { x.CreatedAt, x.Id });
        var attempts = builder.Entity<UpstreamAttempt>();
        attempts.ToTable("upstream_attempts").HasKey(x => x.Id);
        attempts.Property(x => x.Provider).HasMaxLength(64);
        attempts.Property(x => x.Model).HasMaxLength(256);
        attempts.Property(x => x.ProviderKeyId).HasMaxLength(36);
        attempts.Property(x => x.ProviderRequestId).HasMaxLength(256);
        attempts.Property(x => x.Status).HasMaxLength(32);
        attempts.Property(x => x.EstimatedCost).HasPrecision(20, 9);
        attempts.Property(x => x.ActualCost).HasPrecision(20, 9);
        attempts.HasIndex(x => new { x.RequestId, x.CreatedAt });
        attempts.HasIndex(x => new { x.CreatedAt, x.Provider, x.ProviderKeyId });
        var reconciliation = builder.Entity<ReconciliationRecord>();
        reconciliation.ToTable("billing_reconciliations").HasKey(x => x.Reference);
        reconciliation.Property(x => x.Reference).HasMaxLength(128);
        reconciliation.Property(x => x.Actor).HasMaxLength(160);
        reconciliation.Property(x => x.ActualCost).HasPrecision(20, 9);
        reconciliation.HasIndex(x => x.AttemptId).IsUnique();

        var files = builder.Entity<GatewayFile>();
        files.ToTable("gateway_files").HasKey(x => x.Id);
        files.Property(x => x.Id).HasMaxLength(64);
        files.Property(x => x.Filename).HasMaxLength(128);
        files.Property(x => x.Purpose).HasMaxLength(24);
        files.Property(x => x.BatchId).HasMaxLength(64);
        files.HasIndex(x => new { x.ApiKeyId, x.CreatedAt, x.Id });
        var batches = builder.Entity<BatchJob>();
        batches.ToTable("batch_jobs").HasKey(x => x.Id);
        batches.Property(x => x.Id).HasMaxLength(64);
        batches.Property(x => x.Status).HasMaxLength(24);
        batches.Property(x => x.Endpoint).HasMaxLength(64);
        batches.HasIndex(x => new { x.ApiKeyId, x.CreatedAt, x.Id });
        batches.HasOne<ApiKey>().WithMany().HasForeignKey(x => x.ApiKeyId).OnDelete(DeleteBehavior.Restrict);
        var items = builder.Entity<BatchItem>();
        items.ToTable("batch_items").HasKey(x => x.Id);
        items.Property(x => x.BatchId).HasMaxLength(64);
        items.Property(x => x.CustomId).HasMaxLength(128);
        items.Property(x => x.LeaseOwner).HasMaxLength(64);
        items.Property(x => x.Status).HasMaxLength(24);
        items.HasIndex(x => new { x.BatchId, x.Index }).IsUnique();
        items.HasIndex(x => new { x.Status, x.LeaseExpiresAt });
        items.HasOne<BatchJob>().WithMany().HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);

        // SQLite cannot order DateTimeOffset natively. Production PostgreSQL uses timestamptz.
        if (Database.IsSqlite())
        {
            foreach (var entity in builder.Model.GetEntityTypes())
                foreach (var property in entity.GetProperties())
                {
                    if (property.ClrType == typeof(DateTimeOffset))
                        property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
                            value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero)));
                    else if (property.ClrType == typeof(DateTimeOffset?))
                        property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?>(
                            value => value.HasValue ? value.Value.UtcTicks : null,
                            value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null));
                }
        }
    }
}

public sealed class PostgresGatewayDbContext(DbContextOptions<PostgresGatewayDbContext> options) : GatewayDbContext(options);
public sealed class SqliteGatewayDbContext(DbContextOptions<SqliteGatewayDbContext> options) : GatewayDbContext(options);

public sealed class GatewayContextFactory<T>(IDbContextFactory<T> factory) : IDbContextFactory<GatewayDbContext> where T : GatewayDbContext
{
    public GatewayDbContext CreateDbContext() => factory.CreateDbContext();
    public async Task<GatewayDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => await factory.CreateDbContextAsync(cancellationToken);
}

public sealed class PostgresDesignFactory : IDesignTimeDbContextFactory<PostgresGatewayDbContext>
{
    public PostgresGatewayDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<PostgresGatewayDbContext>()
        .UseNpgsql("Host=localhost;Database=llmproxy_design;Username=postgres").Options);
}

public sealed class SqliteDesignFactory : IDesignTimeDbContextFactory<SqliteGatewayDbContext>
{
    public SqliteGatewayDbContext CreateDbContext(string[] args) => new(new DbContextOptionsBuilder<SqliteGatewayDbContext>()
        .UseSqlite("Data Source=llmproxy_design.db").Options);
}
