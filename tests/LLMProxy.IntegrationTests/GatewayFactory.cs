using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using LLMProxy.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LLMProxy.IntegrationTests;

public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    private readonly string[] _providers = ["openai", "anthropic"];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "llmproxy-http-tests-" + Guid.NewGuid().ToString("N"));
    public const string AdminKey = "admin-integration-fixture-only-not-a-live-secret";
    public FakeBackend Backend { get; } = new();
    public LogCapture Logs { get; } = new();

    public GatewayFactory() { }
    internal GatewayFactory(bool configured) => _providers = configured ? ["openai", "anthropic"] : [];
    internal GatewayFactory(params string[] providers) => _providers = providers;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_directory);
        builder.UseEnvironment("Testing");
        var settings = new Dictionary<string, string?>
        {
            ["LLMProxy:Storage:Mode"] = "Standalone",
            ["LLMProxy:Storage:SqlitePath"] = Path.Combine(_directory, "gateway.db"),
            ["LLMProxy:Providers:Resilience:RetryCount"] = "0",
            ["LLMPROXY_ADMIN_KEY"] = AdminKey,
            ["LLMPROXY_BOOTSTRAP_KEY"] = "",
            ["FOUNDRY_AUTHENTICATION"] = "ApiKey",
            ["FOUNDRY_TOKEN_SCOPE"] = "https://ai.azure.com/.default",
            ["OLLAMA_ALLOW_INSECURE_HTTP"] = "false"
        };
        var connections = new (string Name, string Section, string Prefix, string Path)[]
        {
            ("openai", "OpenAI", "OPENAI", "/v1"), ("anthropic", "Anthropic", "ANTHROPIC", "/v1"),
            ("gemini", "Gemini", "GEMINI", "/v1"), ("azure", "AzureOpenAI", "AZURE_OPENAI", ""),
            ("foundry", "Foundry", "FOUNDRY", "/openai/v1"), ("mistral", "Mistral", "MISTRAL", "/v1"),
            ("cohere", "Cohere", "COHERE", "/v2"), ("deepseek", "DeepSeek", "DEEPSEEK", "/v1"),
            ("groq", "Groq", "GROQ", "/openai/v1"), ("ollama", "Ollama", "OLLAMA", "/v1")
        };
        foreach (var (name, section, prefix, path) in connections)
        {
            var enabled = _providers.Contains(name);
            settings[prefix + "_API_KEY"] = enabled && name != "ollama" ? "fake-provider-secret" : "";
            settings["LLMProxy:Providers:" + section + ":ApiKey"] = "";
            settings[prefix + "_ENDPOINT"] = name == "ollama" && !enabled ? "" : "https://" + name + ".test" + path;
            settings["LLMProxy:Providers:" + section + ":BaseUrl"] = settings[prefix + "_ENDPOINT"];
        }
        foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
        builder.ConfigureServices(services =>
        {
            foreach (var (name, _, _, _) in connections)
                services.AddHttpClient("llmproxy." + name).ConfigurePrimaryHttpMessageHandler(() => new BackendHandler(Backend));
            services.AddSingleton<ILoggerProvider>(Logs);
        });
    }

    public async Task<(HttpClient Client, CreatedApiKey Key)> ClientAsync(int rpm = 60, long? tokens = null, decimal? budget = null, string[]? models = null)
    {
        var key = await Services.GetRequiredService<ApiKeyService>().CreateAsync(
            new CreateApiKey("test-" + Guid.NewGuid().ToString("N"), models ?? ["fast"], rpm, tokens, budget), default);
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Key);
        return (client, key);
    }

    public HttpClient AdminClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AdminKey);
        return client;
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

public sealed class FakeBackend
{
    public string Mode { get; set; } = "success";
    public ConcurrentQueue<string> Hosts { get; } = new();
    public ConcurrentQueue<string> Bodies { get; } = new();
    public void Reset(string mode = "success") { Mode = mode; Hosts.Clear(); Bodies.Clear(); }

    public async Task<HttpResponseMessage> RespondAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        Hosts.Enqueue(request.RequestUri!.Host);
        Bodies.Enqueue(body);
        if (Mode == "failure" || Mode == "fallback" && request.RequestUri.Host == "openai.test")
            return new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("upstream-secret and confidential prompt must never escape") };
        using var json = JsonDocument.Parse(body);
        var streaming = json.RootElement.TryGetProperty("stream", out var stream) && stream.GetBoolean();
        var response = request.RequestUri.Host switch
        {
            "anthropic.test" => """{"id":"msg_fake","type":"message","content":[{"type":"text","text":"Hello"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":2}}""",
            "cohere.test" => """{"id":"cohere_fake","finish_reason":"COMPLETE","message":{"role":"assistant","content":[{"type":"text","text":"Hello"}]},"usage":{"tokens":{"input_tokens":3,"output_tokens":2}}}""",
            _ => """{"id":"chatcmpl_fake","object":"chat.completion","created":1,"model":"upstream-model","choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}"""
        };
        if (streaming)
        {
            response = "data: {\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"Hello\"}}]}\n\n";
            response += Mode == "midstream"
                ? "data: {\"error\":{\"message\":\"confidential prompt\"}}\n\n"
                : "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\ndata: {\"choices\":[],\"usage\":{\"prompt_tokens\":3,\"completion_tokens\":2,\"total_tokens\":5}}\n\ndata: [DONE]\n\n";
            if (request.RequestUri.Host == "cohere.test")
                response = "data: {\"type\":\"message-start\",\"delta\":{\"message\":{\"role\":\"assistant\"}}}\n\n"
                    + "data: {\"type\":\"content-delta\",\"delta\":{\"message\":{\"content\":{\"text\":\"Hello\"}}}}\n\n"
                    + "data: {\"type\":\"message-end\",\"delta\":{\"finish_reason\":\"COMPLETE\",\"usage\":{\"tokens\":{\"input_tokens\":3,\"output_tokens\":2}}}}\n\n";
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, streaming ? "text/event-stream" : "application/json") };
    }
}

internal sealed class BackendHandler(FakeBackend backend) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => backend.RespondAsync(request, cancellationToken);
}

public sealed class LogCapture : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => new CaptureLogger(Messages);
    public void Dispose() { }
    private sealed class CaptureLogger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => messages.Enqueue(formatter(state, exception));
    }
}
