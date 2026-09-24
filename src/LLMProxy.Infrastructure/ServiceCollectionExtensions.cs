using LLMProxy.Domain;
using LLMProxy.Infrastructure.Persistence;
using LLMProxy.Infrastructure.RateLimiting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;

namespace LLMProxy.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLlmProxyInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("LLMProxy:Storage").Get<StorageOptions>() ?? new();
        if (!options.IsStandalone && !options.Mode.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Storage mode must be Standalone or PostgreSql.");
        if (string.IsNullOrWhiteSpace(options.RedisKeyPrefix) || options.RedisKeyPrefix.IndexOfAny(['{', '}']) >= 0)
            throw new InvalidOperationException("Invalid Redis key prefix.");
        services.AddSingleton(options);
        if (options.IsStandalone)
        {
            var path = Path.GetFullPath(options.SqlitePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var connection = new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 30, ForeignKeys = true }.ToString();
            services.AddPooledDbContextFactory<SqliteGatewayDbContext>(builder => builder.UseSqlite(connection));
            services.AddSingleton<IDbContextFactory<GatewayDbContext>, GatewayContextFactory<SqliteGatewayDbContext>>();
            services.AddSingleton<IRateLimiter, MemoryRateLimiter>();
            services.AddSingleton<IRoutingState, MemoryRoutingState>();
        }
        else
        {
            var connection = configuration.GetConnectionString("Postgres");
            var redis = configuration.GetConnectionString("Redis");
            if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(redis))
                throw new InvalidOperationException("PostgreSql mode requires ConnectionStrings:Postgres and ConnectionStrings:Redis.");
            services.AddPooledDbContextFactory<PostgresGatewayDbContext>(builder =>
                builder.UseNpgsql(connection, postgres => postgres.EnableRetryOnFailure(3)));
            services.AddSingleton<IDbContextFactory<GatewayDbContext>, GatewayContextFactory<PostgresGatewayDbContext>>();
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var redisOptions = ConfigurationOptions.Parse(redis);
                redisOptions.AbortOnConnectFail = false;
                redisOptions.ConnectTimeout = 5000;
                redisOptions.AsyncTimeout = 5000;
                return ConnectionMultiplexer.Connect(redisOptions);
            });
            services.AddSingleton<IRateLimiter, RedisRateLimiter>();
            services.AddSingleton<IRoutingState, RedisRoutingState>();
            services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
        }
        services.AddSingleton<IGatewayStore, EfGatewayStore>();
        services.AddSingleton<StorageInitializer>();
        services.AddHostedService<ReservationRecoveryService>();
        services.AddHealthChecks().AddCheck<StorageHealthCheck>(options.IsStandalone ? "sqlite" : "postgresql", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
        return services;
    }
}
