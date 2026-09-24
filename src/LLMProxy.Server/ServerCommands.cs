using System.Globalization;
using System.Text.Json;
using LLMProxy.Application;
using LLMProxy.Domain;

namespace LLMProxy.Server;

public static class ServerCommands
{
    public static async Task<int> HealthCheckAsync()
    {
        var url = Environment.GetEnvironmentVariable("LLMPROXY_HEALTH_URL");
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';')[0] ?? "4000";
        if (url is null)
        {
            var configured = Environment.GetEnvironmentVariable("ASPNETCORE_URLS")?.Split(';')
                .FirstOrDefault(value => value.StartsWith("http://", StringComparison.Ordinal));
            if (configured is not null && Uri.TryCreate(configured.Replace("+", "localhost").Replace("*", "localhost"), UriKind.Absolute, out var parsed))
                port = parsed.Port.ToString(CultureInfo.InvariantCulture);
            url = $"http://127.0.0.1:{port}/health/live";
        }
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) };
        try { using var result = await client.GetAsync(url); return result.IsSuccessStatusCode ? 0 : 1; }
        catch (HttpRequestException) { return 1; }
        catch (TaskCanceledException) { return 1; }
    }

    public static async Task<int> RunAsync(string[] args, IServiceProvider services)
    {
        if (args is ["migrate"]) { Console.WriteLine("Database migrations applied."); return 0; }
        var keys = services.GetRequiredService<ApiKeyService>();
        var store = services.GetRequiredService<IGatewayStore>();
        if (args is ["keys", "list"])
        {
            Console.WriteLine(JsonSerializer.Serialize((await store.ListKeysAsync(1000, CancellationToken.None)).Select(ApiKeySummary.From), LlmJson.Options));
            return 0;
        }
        if (args.Length >= 2 && args[1] == "create")
        {
            var values = ParseOptions(args[2..]);
            var command = new CreateApiKey(values.GetValueOrDefault("owner") ?? "local",
                (values.GetValueOrDefault("models") ?? "*").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                int.Parse(values.GetValueOrDefault("rpm") ?? "60", CultureInfo.InvariantCulture),
                values.TryGetValue("token-limit", out var tokens) ? long.Parse(tokens, CultureInfo.InvariantCulture) : null,
                values.TryGetValue("budget", out var budget) ? decimal.Parse(budget, CultureInfo.InvariantCulture) : null);
            var created = await keys.CreateAsync(command, CancellationToken.None);
            // Intentional one-time key disclosure to the operator's terminal; never emitted through logging.
            Console.WriteLine(JsonSerializer.Serialize(created, LlmJson.Options));
            return 0;
        }
        if (args is ["keys", "disable", var id] && Guid.TryParse(id, out var keyId))
        {
            var key = await store.FindKeyByIdAsync(keyId, CancellationToken.None);
            if (key is null) { Console.Error.WriteLine("API key not found."); return 1; }
            var result = await keys.UpdateAsync(keyId, new UpdateApiKey(false, key.AllowedModels, key.RequestsPerMinute,
                key.TokenLimit, key.BudgetUnits is { } budget ? Money.FromUnits(budget) : null), CancellationToken.None);
            Console.WriteLine(JsonSerializer.Serialize(result, LlmJson.Options));
            return 0;
        }
        Console.Error.WriteLine("Usage: keys create [--owner name] [--models fast,reasoning] [--rpm 60] [--token-limit N] [--budget USD] | keys list | keys disable ID | migrate");
        return 1;
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || args[index] is not ("--owner" or "--models" or "--rpm" or "--token-limit" or "--budget"))
                throw new GatewayException("Unknown command option or missing value.", "invalid_command");
            result.Add(args[index][2..], args[index + 1]);
        }
        return result;
    }
}
