using System.Net;
using System.Text;
using LLMProxy.Domain;
using LLMProxy.Providers;

namespace LLMProxy.ProviderTests;

internal sealed class CallbackHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> callback) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => callback(request, cancellationToken);
}

internal sealed class ProviderHarness : IHttpClientFactory, IDisposable
{
    private readonly HttpClient _client;
    public ProviderOptions Options { get; } = new()
    {
        OpenAI = Connection(),
        Anthropic = Connection(),
        Gemini = Connection(),
        AzureOpenAI = new AzureConnectionOptions { BaseUrl = "https://example.test/", ApiKey = "fake-provider-secret" },
        Foundry = new FoundryConnectionOptions { BaseUrl = "https://example.test/", ApiKey = "fake-provider-secret" },
        Mistral = Connection(),
        Cohere = Connection("/v2"),
        DeepSeek = Connection(),
        Groq = Connection("/openai/v1"),
        Ollama = new OllamaConnectionOptions { BaseUrl = "https://example.test/v1", ApiKey = "fake-provider-secret" }
    };
    public string? Body { get; private set; }
    public Uri? Uri { get; private set; }
    public Dictionary<string, string> Headers { get; private set; } = [];

    public ProviderHarness(string response, string contentType = "application/json", HttpStatusCode status = HttpStatusCode.OK)
    {
        _client = new HttpClient(new CallbackHandler(async (request, ct) =>
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            Uri = request.RequestUri;
            Headers = request.Headers.ToDictionary(x => x.Key, x => string.Join(",", x.Value), StringComparer.OrdinalIgnoreCase);
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, contentType) };
        }));
    }
    public HttpClient CreateClient(string name) => _client;
    public ILlmProvider Provider(string name) => name switch
    {
        "openai" => new OpenAiProvider(new(this), Options),
        "anthropic" => new AnthropicProvider(new(this), Options),
        "gemini" => new GeminiProvider(new(this), Options),
        "azure" => new AzureOpenAiProvider(new(this), Options),
        "foundry" => new FoundryProvider(new(this), Options),
        "mistral" => new MistralProvider(new(this), Options),
        "cohere" => new CohereProvider(new(this), Options),
        "deepseek" => new DeepSeekProvider(new(this), Options),
        "groq" => new GroqProvider(new(this), Options),
        "ollama" => new OllamaProvider(new(this), Options),
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };
    public static LlmRequest Request(bool stream = false) => new()
    {
        Model = "upstream-model",
        Messages = [ChatMessage.FromText("system", "Be helpful"), ChatMessage.FromText("user", "Hello")],
        Stream = stream,
        MaxTokens = 128
    };
    public void Dispose() => _client.Dispose();
    private static ProviderConnectionOptions Connection(string path = "/v1") => new() { BaseUrl = "https://example.test" + path, ApiKey = "fake-provider-secret" };
}

internal static class Responses
{
    public const string OpenAi = """{"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"upstream-model","choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""";
    public const string Anthropic = """{"id":"msg-test","type":"message","role":"assistant","content":[{"type":"text","text":"Hello"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":2}}""";
    public const string Gemini = """{"candidates":[{"content":{"parts":[{"text":"Hello"}],"role":"model"},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":2,"totalTokenCount":5}}""";
    public const string Cohere = """{"id":"cohere-test","finish_reason":"COMPLETE","message":{"role":"assistant","content":[{"type":"text","text":"Hello"}]},"usage":{"tokens":{"input_tokens":3,"output_tokens":2},"billed_units":{"input_tokens":1,"output_tokens":1}}}""";
    public static string Body(string provider) => provider switch { "anthropic" => Anthropic, "gemini" => Gemini, "cohere" => Cohere, _ => OpenAi };

    public static string Stream(string provider) => provider switch
    {
        "cohere" => """
            event: message-start
            data: {"type":"message-start","delta":{"message":{"role":"assistant"}}}

            event: content-delta
            data: {"type":"content-delta","index":0,"delta":{"message":{"content":{"type":"text","text":"Hello"}}}}

            event: message-end
            data: {"type":"message-end","delta":{"finish_reason":"COMPLETE","usage":{"tokens":{"input_tokens":3,"output_tokens":2},"billed_units":{"input_tokens":1,"output_tokens":1}}}}


            """,
        "anthropic" => """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":3,"output_tokens":0}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}

            event: message_stop
            data: {"type":"message_stop"}


            """,
        "gemini" => """
            data: {"candidates":[{"content":{"parts":[{"text":"Hello"}],"role":"model"}}]}

            data: {"candidates":[{"content":{"parts":[],"role":"model"},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":2}}


            """,
        _ => """
            : keep-alive

            data: {"id":"1","choices":[{"index":0,"delta":{"role":"assistant"},"finish_reason":null}]}

            data: {"id":"1","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}

            data: {"id":"1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: {"id":"1","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}

            data: [DONE]


            """
    };
}
