namespace LLMProxy.Infrastructure;

public sealed class StorageOptions
{
    public string Mode { get; set; } = "Standalone";
    public string SqlitePath { get; set; } = "data/llmproxy.db";
    public bool AutoMigrate { get; set; } = true;
    public string RedisKeyPrefix { get; set; } = "llmproxy";
    public bool IsStandalone => Mode.Equals("Standalone", StringComparison.OrdinalIgnoreCase);
}
