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

    protected override void OnModelCreating(ModelBuilder builder)
    {
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

        var reservations = builder.Entity<QuotaReservation>();
        reservations.ToTable("quota_reservations");
        reservations.HasKey(x => x.RequestId);
        reservations.HasOne<ApiKey>().WithMany().HasForeignKey(x => x.ApiKeyId).OnDelete(DeleteBehavior.Restrict);
        reservations.Property(x => x.Model).HasMaxLength(128);
        reservations.HasIndex(x => x.ExpiresAt);

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
