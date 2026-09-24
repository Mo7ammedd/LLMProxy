using System.Text.Json;
using LLMProxy.Domain;

namespace LLMProxy.ProviderTests;

public sealed class ProviderDialectTests
{
    [Theory]
    [InlineData("mistral")]
    [InlineData("cohere")]
    [InlineData("deepseek")]
    [InlineData("groq")]
    [InlineData("ollama")]
    public async Task Developer_instructions_are_mapped_to_supported_system_roles(string name)
    {
        using var harness = new ProviderHarness(Responses.Body(name));
        var request = ProviderHarness.Request() with
        { Messages = [ChatMessage.FromText("developer", "Be helpful"), ChatMessage.FromText("user", "Hi")] };
        await harness.Provider(name).ChatCompletionAsync(request, default);
        using var json = JsonDocument.Parse(harness.Body!);
        var message = json.RootElement.GetProperty("messages")[0];
        Assert.Equal("system", message.GetProperty("role").GetString());
        Assert.Equal("Be helpful", message.GetProperty("content").GetString());
    }

    [Fact]
    public async Task Mistral_maps_sampling_and_normalizes_native_content_and_tool_arguments()
    {
        const string response = """{"choices":[{"index":0,"message":{"role":"assistant","content":[{"type":"text","text":"Checking"}],"tool_calls":[{"id":"abc123xyz","type":"function","function":{"name":"weather","arguments":{"city":"Paris"}}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}""";
        using var harness = new ProviderHarness(response);
        var result = await harness.Provider("mistral").ChatCompletionAsync(ProviderHarness.Request() with
        { Seed = 42, MaxTokens = null, MaxCompletionTokens = 55, TopP = 0.8 }, default);
        using var body = JsonDocument.Parse(harness.Body!);
        Assert.Equal(42, body.RootElement.GetProperty("random_seed").GetInt32());
        Assert.False(body.RootElement.TryGetProperty("seed", out _));
        Assert.Equal(55, body.RootElement.GetProperty("max_tokens").GetInt32());
        var choice = Assert.Single(result.Choices);
        Assert.Equal(JsonValueKind.String, choice.Message.Content!.Value.ValueKind);
        Assert.Equal("Checking", choice.Message.Text());
        var tool = Assert.Single(choice.Message.ToolCalls!);
        using var arguments = JsonDocument.Parse(tool.Function.Arguments);
        Assert.Equal("Paris", arguments.RootElement.GetProperty("city").GetString());
    }

    [Fact]
    public async Task Mistral_streams_native_text_blocks_and_model_length_finishes()
    {
        const string response = """
            data: {"choices":[{"index":0,"delta":{"role":"assistant","content":[{"type":"text","text":"Hello"}]}}]}

            data: {"choices":[{"index":0,"delta":null,"finish_reason":"model_length"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}

            data: [DONE]


            """;
        using var harness = new ProviderHarness(response, "text/event-stream");
        var chunks = await StreamAsync(harness, "mistral");
        Assert.Equal("Hello", string.Concat(chunks.Select(chunk => chunk.Delta?.Content)));
        Assert.Contains(chunks, chunk => chunk.FinishReason == "length");
        Assert.Equal(TokenUsage.From(3, 2), chunks.Last(chunk => chunk.Usage is not null).Usage);
    }

    [Fact]
    public async Task Mistral_maps_foreign_tool_ids_consistently_without_mutating_client_history()
    {
        using var harness = new ProviderHarness(Responses.OpenAi);
        var request = ProviderHarness.Request() with
        {
            Messages = [ChatMessage.FromText("user", "Weather?"), ChatMessage.FromText("assistant", null) with
            {
                ToolCalls = [new ToolCall("call_openai_long_id", "function", new FunctionCall("weather", "{}")),
                    new ToolCall("abc123xyz", "function", new FunctionCall("weather", "{}"))]
            }, ChatMessage.FromText("tool", "Paris") with { ToolCallId = "call_openai_long_id" },
                ChatMessage.FromText("tool", "London") with { ToolCallId = "abc123xyz" }],
            Tools = [new ToolDefinition("function", new FunctionDefinition("weather", null, null))]
        };
        await harness.Provider("mistral").ChatCompletionAsync(request, default);
        using var json = JsonDocument.Parse(harness.Body!);
        var messages = json.RootElement.GetProperty("messages");
        var id = messages[1].GetProperty("tool_calls")[0].GetProperty("id").GetString()!;
        Assert.Matches("^[A-Za-z0-9]{9}$", id);
        Assert.Equal(id, messages[2].GetProperty("tool_call_id").GetString());
        Assert.Equal("abc123xyz", messages[3].GetProperty("tool_call_id").GetString());
        Assert.Equal("call_openai_long_id", request.Messages[2].ToolCallId);
        Assert.Equal("object", json.RootElement.GetProperty("tools")[0].GetProperty("function").GetProperty("parameters").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Groq_reads_final_usage_from_x_groq_without_a_stream_options_parameter()
    {
        const string response = """
            data: {"choices":[{"index":0,"delta":{"role":"assistant","content":"Hello"}}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"x_groq":{"usage":{"prompt_tokens":9,"completion_tokens":4}}}

            data: [DONE]


            """;
        using var harness = new ProviderHarness(response, "text/event-stream");
        var chunks = await StreamAsync(harness, "groq");
        Assert.Equal(TokenUsage.From(9, 4), chunks.Last(chunk => chunk.Usage is not null).Usage);
        Assert.DoesNotContain("stream_options", harness.Body!);
    }

    [Fact]
    public async Task DeepSeek_preserves_reasoning_with_tool_history_and_terminal_chunk_usage()
    {
        const string response = """{"choices":[{"index":0,"message":{"role":"assistant","content":null,"reasoning_content":"Need a weather lookup","tool_calls":[{"id":"call_1","type":"function","function":{"name":"weather","arguments":"{}"}}]},"finish_reason":"tool_calls"}],"usage":{"prompt_tokens":3,"completion_tokens":8}}""";
        using var harness = new ProviderHarness(response);
        var result = await harness.Provider("deepseek").ChatCompletionAsync(ProviderHarness.Request(), default);
        var message = result.Choices[0].Message;
        Assert.Equal("Need a weather lookup", message.ReasoningContent);
        await harness.Provider("deepseek").ChatCompletionAsync(ProviderHarness.Request() with
        {
            Messages = [ChatMessage.FromText("user", "Weather?"), message,
                ChatMessage.FromText("tool", "Sunny") with { ToolCallId = "call_1" }]
        }, default);
        Assert.Contains("Need a weather lookup", harness.Body!);

        const string streaming = """
            data: {"choices":[{"index":0,"delta":{"role":"assistant","reasoning_content":"Thinking"}}]}

            data: {"choices":[{"index":0,"delta":{"content":"Hello"}}]}

            data: {"choices":[{"index":0,"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":8}}

            data: [DONE]


            """;
        using var streamHarness = new ProviderHarness(streaming, "text/event-stream");
        var chunks = await StreamAsync(streamHarness, "deepseek");
        Assert.Equal("Thinking", string.Concat(chunks.Select(chunk => chunk.Delta?.ReasoningContent)));
        Assert.Equal("Hello", string.Concat(chunks.Select(chunk => chunk.Delta?.Content)));
        Assert.Equal(TokenUsage.From(3, 8), chunks.Last(chunk => chunk.Usage is not null).Usage);
    }

    [Theory]
    [InlineData("insufficient_system_resource")]
    [InlineData("aborted")]
    public async Task DeepSeek_interrupted_generations_are_errors_in_both_response_modes(string reason)
    {
        using var normal = new ProviderHarness(Responses.OpenAi.Replace("\"stop\"", "\"" + reason + "\"", StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<ProviderException>(() => normal.Provider("deepseek").ChatCompletionAsync(ProviderHarness.Request(), default));
        Assert.True(error.IsTransient);
        Assert.Equal("provider_unavailable", error.Code);
        using var streaming = new ProviderHarness(Responses.Stream("deepseek").Replace("\"stop\"", "\"" + reason + "\"", StringComparison.Ordinal), "text/event-stream");
        error = await Assert.ThrowsAsync<ProviderException>(() => StreamAsync(streaming, "deepseek"));
        Assert.True(error.IsTransient);
        Assert.Equal("provider_unavailable", error.Code);
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("azure")]
    [InlineData("anthropic")]
    [InlineData("gemini")]
    [InlineData("foundry")]
    [InlineData("mistral")]
    [InlineData("cohere")]
    [InlineData("groq")]
    [InlineData("ollama")]
    public async Task Reasoning_history_is_not_sent_to_providers_that_do_not_accept_it(string name)
    {
        using var harness = new ProviderHarness(Responses.Body(name));
        await harness.Provider(name).ChatCompletionAsync(ProviderHarness.Request() with
        {
            Messages = [ChatMessage.FromText("user", "First question"),
                ChatMessage.FromText("assistant", "First answer") with { ReasoningContent = "provider-specific-reasoning" },
                ChatMessage.FromText("user", "Next question")]
        }, default);
        Assert.DoesNotContain("reasoning_content", harness.Body!);
        Assert.DoesNotContain("provider-specific-reasoning", harness.Body!);
    }

    [Theory]
    [InlineData("foundry")]
    [InlineData("mistral")]
    [InlineData("deepseek")]
    [InlineData("groq")]
    [InlineData("ollama")]
    public async Task Compatible_tool_streams_keep_function_arguments_and_normalize_stop(string name)
    {
        const string response = """
            data: {"choices":[{"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"weather","arguments":"{"}}]}}]}

            data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"city\":\"Paris\"}"}}]}}]}

            data: {"choices":[{"delta":{},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":2}}

            data: [DONE]


            """;
        using var harness = new ProviderHarness(response, "text/event-stream");
        var chunks = await StreamAsync(harness, name);
        var tools = chunks.SelectMany(chunk => chunk.Delta?.ToolCalls ?? []).ToArray();
        Assert.Equal("call_1", tools[0].Id);
        Assert.Equal("weather", tools[0].Function!.Name);
        Assert.All(tools, tool => Assert.Equal(0, tool.Index));
        using var arguments = JsonDocument.Parse(string.Concat(tools.Select(tool => tool.Function?.Arguments)));
        Assert.Equal("Paris", arguments.RootElement.GetProperty("city").GetString());
        Assert.Contains(chunks, chunk => chunk.FinishReason == "tool_calls");
    }

    [Theory]
    [InlineData("auto", true)]
    [InlineData("none", false)]
    public async Task Ollama_omits_unsupported_tool_choice_and_can_disable_offered_tools(string choice, bool toolsExpected)
    {
        using var harness = new ProviderHarness(Responses.OpenAi);
        harness.Options.Ollama.ApiKey = "";
        harness.Options.Ollama.BaseUrl = "http://localhost:11434";
        harness.Options.Ollama.AllowInsecureHttp = true;
        var request = ProviderHarness.Request() with { Tools = [WeatherTool()], ToolChoice = JsonSerializer.SerializeToElement(choice) };
        Assert.True(harness.Provider("ollama").IsConfigured);
        await harness.Provider("ollama").ChatCompletionAsync(request, default);
        Assert.Equal("/v1/chat/completions", harness.Uri!.AbsolutePath);
        Assert.False(harness.Headers.ContainsKey("Authorization"));
        using var json = JsonDocument.Parse(harness.Body!);
        Assert.False(json.RootElement.TryGetProperty("tool_choice", out _));
        Assert.Equal(toolsExpected, json.RootElement.TryGetProperty("tools", out _));
    }

    [Theory]
    [InlineData("ollama", "{\"tool_choice\":\"required\"}", "tool_choice")]
    [InlineData("ollama", "{\"tool_choice\":{\"type\":\"function\",\"function\":{\"name\":\"weather\"}}}", "tool_choice")]
    [InlineData("ollama", "{\"parallel_tool_calls\":false}", "parallel_tool_calls")]
    [InlineData("deepseek", "{\"seed\":1}", "seed")]
    [InlineData("deepseek", "{\"response_format\":{\"type\":\"json_schema\"}}", "response_format")]
    [InlineData("deepseek", "{\"parallel_tool_calls\":false}", "parallel_tool_calls")]
    [InlineData("groq", "{\"messages\":[{\"role\":\"user\",\"content\":\"Hi\",\"name\":\"caller\"}]}", "messages.name")]
    public async Task Unsupported_dialect_parameters_fail_before_sending_a_request(string name, string json, string parameter)
    {
        using var harness = new ProviderHarness(Responses.Body(name));
        var request = JsonSerializer.Deserialize<LlmRequest>(json, LlmJson.Options)!;
        var error = await Assert.ThrowsAsync<GatewayException>(() => harness.Provider(name).ChatCompletionAsync(request, default));
        Assert.Equal("unsupported_parameter", error.Code);
        Assert.Equal(parameter, error.Param);
        Assert.Null(harness.Body);
    }

    internal static ToolDefinition WeatherTool(bool? strict = null) => new("function", new FunctionDefinition("weather", "Current weather",
        JsonSerializer.SerializeToElement(new { type = "object", properties = new { city = new { type = "string" } }, required = new[] { "city" } }), strict));

    internal static async Task<List<LlmStreamChunk>> StreamAsync(ProviderHarness harness, string name)
    {
        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in harness.Provider(name).StreamChatCompletionAsync(ProviderHarness.Request(true), default)) chunks.Add(chunk);
        return chunks;
    }
}
