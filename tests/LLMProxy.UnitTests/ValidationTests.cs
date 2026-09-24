using System.Text.Json;
using LLMProxy.Application;
using LLMProxy.Domain;

namespace LLMProxy.UnitTests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData("{\"model\":\"fast\",\"messages\":[]}")]
    [InlineData("{\"model\":\"fast\",\"messages\":[null]}")]
    [InlineData("{\"model\":\"fast\",\"messages\":[{\"role\":\"user\",\"content\":true}]}")]
    [InlineData("{\"model\":\"fast\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"image_url\"}]}]}")]
    [InlineData("{\"model\":\"fast\",\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":3,\"text\":\"hello\"}]}]}")]
    [InlineData("{\"model\":\"fast\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"response_format\":\"invalid\"}")]
    [InlineData("{\"model\":\"fast\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"response_format\":{\"type\":\"json_schema\"}}")]
    [InlineData("{\"model\":\"fast\",\"messages\":[{\"role\":\"tool\",\"content\":\"result\",\"tool_call_id\":\"missing\"}]}")]
    [InlineData("{\"model\":\"fast\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"n\":2}")]
    [InlineData("{\"model\":\"fast\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"max_tokens\":-1}")]
    [InlineData("{\"model\":\"fast\",\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"logprobs\":true}")]
    public void Invalid_or_unsupported_requests_receive_client_errors(string json)
    {
        var request = JsonSerializer.Deserialize<LlmRequest>(json, LlmJson.Options)!;
        var validator = new RequestValidator(TestData.Options(), new ModelRegistry(TestData.Options()));
        Assert.Equal(400, Assert.Throws<GatewayException>(() => validator.Validate(request, TestData.Key())).StatusCode);
    }

    [Fact]
    public void Text_parts_and_tool_results_are_validated_without_losing_text()
    {
        var request = TestData.Request() with
        {
            Messages = [new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement(new[] { new { type = "text", text = "Hello" }, new { type = "text", text = " world" } }) }]
        };
        var validator = new RequestValidator(TestData.Options(), new ModelRegistry(TestData.Options()));
        Assert.Equal("Hello world", validator.Validate(request, TestData.Key()).Messages[0].Text());
    }

    [Fact]
    public void Missing_pricing_never_silently_becomes_free()
    {
        var calculator = new CostCalculator(new ConfiguredPricing(TestData.Options()));
        Assert.Throws<InvalidOperationException>(() => calculator.Calculate(new ModelTarget("unknown", "unknown"), TokenUsage.From(1, 1)));
    }
}
