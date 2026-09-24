using System.Text.Json;
using LLMProxy.Domain;

namespace LLMProxy.ProviderTests;

public sealed class CohereTests
{
    [Fact]
    public async Task Native_sampling_stops_tools_and_tool_results_are_translated()
    {
        const string response = """{"id":"cohere-tool","finish_reason":"TOOL_CALL","message":{"role":"assistant","tool_calls":[{"id":"weather_1","type":"function","function":{"name":"weather","arguments":"{\"city\":\"Paris\"}"}}]},"usage":{"billed_units":{"input_tokens":4,"output_tokens":6}}}""";
        using var harness = new ProviderHarness(response);
        var request = ProviderHarness.Request() with
        {
            TopP = 0.8,
            Seed = 42,
            Temperature = 0.4,
            Stop = JsonSerializer.SerializeToElement("STOP"),
            Tools = [ProviderDialectTests.WeatherTool(strict: true)],
            ToolChoice = JsonSerializer.SerializeToElement("required")
        };
        var result = await harness.Provider("cohere").ChatCompletionAsync(request, default);
        using var payload = JsonDocument.Parse(harness.Body!);
        var root = payload.RootElement;
        Assert.Equal(0.8, root.GetProperty("p").GetDouble());
        Assert.Equal(42, root.GetProperty("seed").GetInt32());
        Assert.Equal("STOP", root.GetProperty("stop_sequences")[0].GetString());
        Assert.Equal("REQUIRED", root.GetProperty("tool_choice").GetString());
        Assert.True(root.GetProperty("strict_tools").GetBoolean());
        Assert.False(root.GetProperty("tools")[0].GetProperty("function").TryGetProperty("strict", out _));
        var message = result.Choices[0].Message;
        Assert.Equal("tool_calls", result.Choices[0].FinishReason);
        Assert.Null(message.Content);
        Assert.Equal("weather_1", Assert.Single(message.ToolCalls!).Id);
        Assert.Equal(TokenUsage.From(4, 6), result.Usage);

        await harness.Provider("cohere").ChatCompletionAsync(request with
        {
            Messages = [ChatMessage.FromText("user", "Weather?"), message,
                ChatMessage.FromText("tool", "20 degrees") with { ToolCallId = "weather_1" }]
        }, default);
        using var history = JsonDocument.Parse(harness.Body!);
        var toolResult = history.RootElement.GetProperty("messages")[2];
        Assert.Equal("weather_1", toolResult.GetProperty("tool_call_id").GetString());
        Assert.Equal("text", toolResult.GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("20 degrees", toolResult.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Named_tool_choice_filters_tools_and_requires_the_selected_function()
    {
        using var harness = new ProviderHarness(Responses.Cohere);
        var request = ProviderHarness.Request() with
        {
            Tools = [ProviderDialectTests.WeatherTool(), new ToolDefinition("function", new FunctionDefinition("other", null, null))],
            ToolChoice = JsonSerializer.SerializeToElement(new { type = "function", function = new { name = "weather" } })
        };
        await harness.Provider("cohere").ChatCompletionAsync(request, default);
        using var payload = JsonDocument.Parse(harness.Body!);
        Assert.Equal("REQUIRED", payload.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal("weather", Assert.Single(payload.RootElement.GetProperty("tools").EnumerateArray()).GetProperty("function").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Openai_json_schema_wrapper_is_converted_to_coheres_schema()
    {
        using var harness = new ProviderHarness(Responses.Cohere);
        var request = ProviderHarness.Request() with
        {
            ResponseFormat = JsonSerializer.SerializeToElement(new
            {
                type = "json_schema",
                json_schema = new
                {
                    name = "answer",
                    strict = true,
                    schema = new { type = "object", properties = new { answer = new { type = "string" } }, required = new[] { "answer" } }
                }
            })
        };
        await harness.Provider("cohere").ChatCompletionAsync(request, default);
        using var payload = JsonDocument.Parse(harness.Body!);
        var format = payload.RootElement.GetProperty("response_format");
        Assert.Equal("json_object", format.GetProperty("type").GetString());
        var schema = format.GetProperty("json_schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.True(schema.GetProperty("properties").TryGetProperty("answer", out _));
        Assert.False(schema.TryGetProperty("name", out _));
        Assert.False(schema.TryGetProperty("schema", out _));
    }

    [Fact]
    public async Task Tool_streams_keep_indexes_fragmented_arguments_and_actual_token_usage()
    {
        const string response = """
            event: message-start
            data: {"type":"message-start","delta":{"message":{"role":"assistant"}}}

            event: tool-call-start
            data: {"type":"tool-call-start","index":0,"delta":{"message":{"tool_calls":{"id":"weather_1","type":"function","function":{"name":"weather","arguments":""}}}}}

            event: tool-call-delta
            data: {"type":"tool-call-delta","index":0,"delta":{"message":{"tool_calls":{"function":{"arguments":"{\"city\":"}}}}}

            event: tool-call-delta
            data: {"type":"tool-call-delta","index":0,"delta":{"message":{"tool_calls":{"function":{"arguments":"\"Paris\"}"}}}}}

            event: tool-call-end
            data: {"type":"tool-call-end","index":0}

            event: tool-call-start
            data: {"type":"tool-call-start","index":1,"delta":{"message":{"tool_calls":{"id":"weather_2","type":"function","function":{"name":"weather","arguments":"{\"city\":\"London\"}"}}}}}

            event: tool-call-end
            data: {"type":"tool-call-end","index":1}

            event: message-end
            data: {"type":"message-end","delta":{"finish_reason":"TOOL_CALL","usage":{"tokens":{"input_tokens":25,"output_tokens":12},"billed_units":{"input_tokens":10,"output_tokens":5}}}}


            """;
        using var harness = new ProviderHarness(response, "text/event-stream");
        var chunks = await ProviderDialectTests.StreamAsync(harness, "cohere");
        var calls = chunks.SelectMany(chunk => chunk.Delta?.ToolCalls ?? []).ToArray();
        var first = calls.Where(call => call.Index == 0).ToArray();
        Assert.Equal("weather_1", first[0].Id);
        Assert.Equal("weather", first[0].Function!.Name);
        using var arguments = JsonDocument.Parse(string.Concat(first.Select(call => call.Function?.Arguments)));
        Assert.Equal("Paris", arguments.RootElement.GetProperty("city").GetString());
        Assert.Equal("weather_2", Assert.Single(calls, call => call.Index == 1).Id);
        Assert.Equal("tool_calls", chunks.Last().FinishReason);
        Assert.Equal(TokenUsage.From(25, 12), chunks.Last().Usage);
    }

    [Theory]
    [InlineData("COMPLETE", "stop")]
    [InlineData("STOP_SEQUENCE", "stop")]
    [InlineData("MAX_TOKENS", "length")]
    public async Task Native_finish_reasons_are_normalized(string native, string expected)
    {
        using var harness = new ProviderHarness(Responses.Cohere.Replace("COMPLETE", native, StringComparison.Ordinal));
        Assert.Equal(expected, (await harness.Provider("cohere").ChatCompletionAsync(ProviderHarness.Request(), default)).Choices[0].FinishReason);
    }

    [Theory]
    [InlineData("ERROR", "provider_stream_error")]
    [InlineData("TIMEOUT", "provider_timeout")]
    public async Task In_band_generation_failures_are_transient_and_sanitized(string reason, string code)
    {
        using var harness = new ProviderHarness(Responses.Cohere.Replace("COMPLETE", reason, StringComparison.Ordinal));
        var error = await Assert.ThrowsAsync<ProviderException>(() => harness.Provider("cohere").ChatCompletionAsync(ProviderHarness.Request(), default));
        Assert.True(error.IsTransient);
        Assert.Equal(code, error.Code);
        using var streaming = new ProviderHarness("data: {\"type\":\"message-end\",\"delta\":{\"finish_reason\":\"" + reason + "\",\"message\":\"private data\"}}\n\n", "text/event-stream");
        error = await Assert.ThrowsAsync<ProviderException>(() => ProviderDialectTests.StreamAsync(streaming, "cohere"));
        Assert.True(error.IsTransient);
        Assert.Equal(code, error.Code);
        Assert.DoesNotContain("private data", error.ToString());
    }

    [Theory]
    [InlineData("top_p")]
    [InlineData("parallel_tool_calls")]
    [InlineData("response_format")]
    [InlineData("tools.function.strict")]
    public async Task Unsupported_parameter_combinations_are_explicit_errors(string parameter)
    {
        using var harness = new ProviderHarness(Responses.Cohere);
        var request = ProviderHarness.Request() with { Tools = [ProviderDialectTests.WeatherTool()] };
        request = parameter switch
        {
            "top_p" => request with { TopP = 1 },
            "parallel_tool_calls" => request with { ParallelToolCalls = false },
            "response_format" => request with { ResponseFormat = JsonSerializer.SerializeToElement(new { type = "json_object" }) },
            _ => request with { Tools = [ProviderDialectTests.WeatherTool(true), new ToolDefinition("function", new FunctionDefinition("other", null, null, false))] }
        };
        var error = await Assert.ThrowsAsync<GatewayException>(() => harness.Provider("cohere").ChatCompletionAsync(request, default));
        Assert.Equal("unsupported_parameter", error.Code);
        Assert.Equal(parameter, error.Param);
        Assert.Null(harness.Body);
    }

    [Fact]
    public async Task Out_of_order_tool_fragments_are_rejected()
    {
        const string response = "data: {\"type\":\"tool-call-delta\",\"index\":1,\"delta\":{\"message\":{\"tool_calls\":{\"function\":{\"arguments\":\"{}\"}}}}}\n\n";
        using var harness = new ProviderHarness(response, "text/event-stream");
        var error = await Assert.ThrowsAsync<ProviderException>(() => ProviderDialectTests.StreamAsync(harness, "cohere"));
        Assert.Equal("invalid_provider_response", error.Code);
    }
}
