namespace LLMProxy.Infrastructure;

public sealed class StorageOptions
{
    public string Mode { get; set; } = "Standalone";
    public string SqlitePath { get; set; } = "data/llmproxy.db";
    public bool AutoMigrate { get; set; } = true;
    public string RedisKeyPrefix { get; set; } = "llmproxy";
    public int UsageRetentionDays { get; set; } = 90;
    public int AuditRetentionDays { get; set; } = 365;
    public int BatchRetentionDays { get; set; } = 7;
    public bool IsStandalone => Mode.Equals("Standalone", StringComparison.OrdinalIgnoreCase);
}
