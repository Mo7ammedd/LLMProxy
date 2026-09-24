using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

public sealed class OllamaProvider(ProviderHttpTransport transport, ProviderOptions options)
    : OpenAiCompatibleProvider(transport, options.Ollama)
{
    public override string Name => "ollama";
    protected override string TokenLimitParameter => "max_tokens";
    protected override bool SupportsDeveloperRole => false;
    protected override Uri Endpoint(LlmRequest request) => options.Ollama.Endpoint(
        new Uri(options.Ollama.BaseUrl).AbsolutePath.TrimEnd('/') == "" ? "v1/chat/completions" : "chat/completions");
    protected override Dictionary<string, string> Headers() => string.IsNullOrWhiteSpace(options.Ollama.ApiKey)
        ? [] : base.Headers();

    protected override JsonObject BuildPayload(LlmRequest request)
    {
        if (request.ToolChoice is { ValueKind: JsonValueKind.Object }
            || request.ToolChoice is { ValueKind: JsonValueKind.String } choice && choice.GetString() is not ("auto" or "none"))
            ProviderJson.Unsupported("tool_choice");
        if (request.ParallelToolCalls == false) ProviderJson.Unsupported("parallel_tool_calls");
        if (request.Tools?.Any(tool => tool.Function.Strict == true) == true) ProviderJson.Unsupported("tools.function.strict");
        if (request.Messages.Any(message => message.Name is not null)) ProviderJson.Unsupported("messages.name");
        var payload = base.BuildPayload(request with { User = null, N = null, ParallelToolCalls = null });
        payload.Remove("tool_choice");
        if (request.ToolChoice is { ValueKind: JsonValueKind.String } selected && selected.GetString() == "none") payload.Remove("tools");
        if (payload["tools"] is JsonArray tools)
            foreach (var tool in tools) tool?["function"]?.AsObject().Remove("strict");
        return payload;
    }
}
