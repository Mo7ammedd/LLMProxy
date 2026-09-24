using System.Net;
using System.Text.Json;
using LLMProxy.Domain;

namespace LLMProxy.ProviderTests;

public sealed class ProviderTests
{
    [Theory]
    [InlineData("openai", "/v1/chat/completions", "Authorization")]
    [InlineData("azure", "/openai/deployments/upstream-model/chat/completions", "api-key")]
    [InlineData("anthropic", "/v1/messages", "x-api-key")]
    [InlineData("gemini", "/v1/models/upstream-model:generateContent", "x-goog-api-key")]
    [InlineData("foundry", "/openai/v1/chat/completions", "Authorization")]
    [InlineData("mistral", "/v1/chat/completions", "Authorization")]
    [InlineData("cohere", "/v2/chat", "Authorization")]
    [InlineData("deepseek", "/v1/chat/completions", "Authorization")]
    [InlineData("groq", "/openai/v1/chat/completions", "Authorization")]
    [InlineData("ollama", "/v1/chat/completions", "Authorization")]
    public async Task Native_requests_and_responses_are_translated(string name, string path, string keyHeader)
    {
        using var harness = new ProviderHarness(Responses.Body(name));
        var result = await harness.Provider(name).ChatCompletionAsync(ProviderHarness.Request(), default);
        Assert.Equal("Hello", Assert.Single(result.Choices).Message.Text());
        Assert.Equal(TokenUsage.From(3, 2), result.Usage);
        Assert.Equal(path, harness.Uri!.AbsolutePath);
        Assert.Contains("fake-provider-secret", harness.Headers[keyHeader]);
        Assert.DoesNotContain("fake-provider-secret", harness.Uri.ToString());
        using var body = JsonDocument.Parse(harness.Body!);
        if (name is "openai" or "azure" or "foundry" or "groq")
        {
            Assert.Equal(128, body.RootElement.GetProperty("max_completion_tokens").GetInt32());
            Assert.False(body.RootElement.TryGetProperty("max_tokens", out _));
        }
        if (name is "mistral" or "cohere" or "deepseek" or "ollama")
        {
            Assert.Equal(128, body.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.False(body.RootElement.TryGetProperty("max_completion_tokens", out _));
        }
        if (name == "anthropic")
        {
            Assert.Equal("Be helpful", body.RootElement.GetProperty("system").GetString());
            Assert.Equal(128, body.RootElement.GetProperty("max_tokens").GetInt32());
        }
        if (name == "gemini")
        {
            Assert.Equal("Be helpful", body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
            Assert.Equal(128, body.RootElement.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
        }
        if (name == "azure") Assert.Contains("api-version=2024-10-21", harness.Uri.Query);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("azure")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("foundry")]
    [InlineData("mistral")]
    [InlineData("cohere")]
    [InlineData("deepseek")]
    [InlineData("groq")]
    [InlineData("ollama")]
    public async Task Streams_normalize_text_finish_reasons_and_token_usage(string name)
    {
        using var harness = new ProviderHarness(Responses.Stream(name), "text/event-stream");
        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in harness.Provider(name).StreamChatCompletionAsync(ProviderHarness.Request(true), default)) chunks.Add(chunk);
        Assert.Equal("Hello", string.Concat(chunks.Select(x => x.Delta?.Content)));
        Assert.Contains(chunks, chunk => chunk.Delta?.Role == "assistant");
        Assert.Contains(chunks, chunk => chunk.FinishReason == "stop");
        Assert.Equal(TokenUsage.From(3, 2), chunks.Last(x => x.Usage is not null).Usage);
        if (name is "openai" or "azure" or "foundry" or "deepseek" or "ollama") Assert.Contains("include_usage", harness.Body!);
        if (name is "mistral" or "cohere" or "groq") Assert.DoesNotContain("stream_options", harness.Body!);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("foundry")]
    [InlineData("mistral")]
    [InlineData("cohere")]
    [InlineData("deepseek")]
    [InlineData("groq")]
    [InlineData("ollama")]
    public async Task Truncated_streams_are_errors_instead_of_false_success(string name)
    {
        var data = name switch
        {
            "anthropic" => "data: {\"type\":\"message_start\",\"message\":{}}\n\n",
            "gemini" => "data: {\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"Hi\"}]}}]}\n\n",
            "cohere" => "data: {\"type\":\"message-start\",\"delta\":{\"message\":{\"role\":\"assistant\"}}}\n\n",
            _ => "data: {\"choices\":[{\"delta\":{\"content\":\"Hi\"}}]}\n\n"
        };
        using var harness = new ProviderHarness(data, "text/event-stream");
        var error = await Assert.ThrowsAsync<ProviderException>(async () =>
        {
            await foreach (var _ in harness.Provider(name).StreamChatCompletionAsync(ProviderHarness.Request(true), default)) { }
        });
        Assert.Equal("incomplete_provider_stream", error.Code);
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(503, true)]
    [InlineData(408, true)]
    [InlineData(401, false)]
    [InlineData(400, false)]
    [InlineData(302, false)]
    public async Task Errors_have_safe_messages_and_correct_retry_classification(int status, bool transient)
    {
        using var harness = new ProviderHarness("provider-secret and private prompt", status: (HttpStatusCode)status);
        var exception = await Assert.ThrowsAsync<ProviderException>(() => harness.Provider("openai").ChatCompletionAsync(ProviderHarness.Request(), default));
        Assert.Equal(transient, exception.IsTransient);
        Assert.DoesNotContain("private prompt", exception.ToString());
        Assert.DoesNotContain("provider-secret", exception.ToString());
    }

    [Fact]
    public async Task Anthropic_tool_arguments_and_cached_input_are_normalized()
    {
        const string response = """{"type":"message","id":"msg_1","content":[{"type":"tool_use","id":"call_1","name":"weather","input":{"city":"Paris"}}],"stop_reason":"tool_use","usage":{"input_tokens":3,"cache_read_input_tokens":7,"output_tokens":2}}""";
        using var harness = new ProviderHarness(response);
        var result = await harness.Provider("anthropic").ChatCompletionAsync(ProviderHarness.Request(), default);
        var choice = Assert.Single(result.Choices);
        Assert.Equal("tool_calls", choice.FinishReason);
        var call = Assert.Single(choice.Message.ToolCalls!);
        Assert.Equal("weather", call.Function.Name);
        Assert.Equal("Paris", JsonDocument.Parse(call.Function.Arguments).RootElement.GetProperty("city").GetString());
        Assert.Equal(10, result.Usage.InputTokens);
    }

    [Fact]
    public async Task Gemini_preserves_signatures_and_counts_reasoning_tokens()
    {
        const string response = """{"candidates":[{"content":{"parts":[{"functionCall":{"name":"weather","args":{"city":"Paris"}},"thoughtSignature":"opaque-signature"}]},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":3,"candidatesTokenCount":2,"thoughtsTokenCount":4}}""";
        using var harness = new ProviderHarness(response);
        var result = await harness.Provider("gemini").ChatCompletionAsync(ProviderHarness.Request(), default);
        var call = Assert.Single(result.Choices[0].Message.ToolCalls!);
        Assert.Equal(6, result.Usage.OutputTokens);
        var request = ProviderHarness.Request() with
        {
            Messages = [ChatMessage.FromText("user", "Weather?"), result.Choices[0].Message,
                ChatMessage.FromText("tool", "20 degrees") with { ToolCallId = call.Id }]
        };
        await harness.Provider("gemini").ChatCompletionAsync(request, default);
        Assert.Contains("opaque-signature", harness.Body!);
        Assert.Contains("functionResponse", harness.Body!);
        Assert.DoesNotContain(call.Id, harness.Body!);
    }
}
