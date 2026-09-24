using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

public sealed class DeepSeekProvider(ProviderHttpTransport transport, ProviderOptions options)
    : OpenAiCompatibleProvider(transport, options.DeepSeek)
{
    public override string Name => "deepseek";
    protected override string TokenLimitParameter => "max_tokens";
    protected override bool SupportsDeveloperRole => false;
    protected override bool SupportsReasoningContent => true;
    protected override string FinishReason(string reason) => reason is "insufficient_system_resource" or "aborted"
        ? throw new ProviderException("provider_unavailable", true) : reason;

    protected override JsonObject BuildPayload(LlmRequest request)
    {
        if (request.Seed is not null) ProviderJson.Unsupported("seed");
        if (request.ParallelToolCalls == false) ProviderJson.Unsupported("parallel_tool_calls");
        if (request.ResponseFormat is { } format && format.Text("type") == "json_schema")
            ProviderJson.Unsupported("response_format");
        // Strict tools require DeepSeek's separate beta API, which this adapter does not use.
        if (request.Tools?.Any(tool => tool.Function.Strict == true) == true) ProviderJson.Unsupported("tools.function.strict");
        var payload = request with { User = null, N = null, ParallelToolCalls = null };
        var result = base.BuildPayload(payload);
        if (result["tools"] is JsonArray tools)
            foreach (var tool in tools) tool?["function"]?.AsObject().Remove("strict");
        return result;
    }
}
