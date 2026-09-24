using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

public sealed class MistralProvider(ProviderHttpTransport transport, ProviderOptions options)
    : OpenAiCompatibleProvider(transport, options.Mistral)
{
    public override string Name => "mistral";
    protected override string TokenLimitParameter => "max_tokens";
    protected override bool RequestStreamUsage => false;
    protected override bool SupportsDeveloperRole => false;

    protected override JsonObject BuildPayload(LlmRequest request)
    {
        if (request.Messages.Any(message => message.Name is not null && message.Role != "tool")) ProviderJson.Unsupported("messages.name");
        var payload = base.BuildPayload(request with { User = null });
        payload.Remove("seed");
        if (request.Seed is { } seed) payload["random_seed"] = seed;
        if (payload["tools"] is JsonArray tools)
            foreach (var tool in tools)
                if (tool?["function"] is JsonObject function && function["parameters"] is null)
                    function["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
        NormalizeToolIds(payload);
        return payload;
    }

    protected override string FinishReason(string reason) => reason switch
    {
        "model_length" => "length",
        "error" => throw new ProviderException("provider_stream_error", true),
        _ => reason
    };

    protected override List<ChatChoice>? ReadChoices(JsonElement root)
    {
        if (root.Object("choices").ValueKind != JsonValueKind.Array) throw new ProviderException("invalid_provider_response", false);
        var choices = JsonNode.Parse(root.Object("choices").GetRawText()) as JsonArray
            ?? throw new ProviderException("invalid_provider_response", false);
        foreach (var choice in choices) NormalizeMessage(choice?["message"]);
        return choices.Deserialize<List<ChatChoice>>(LlmJson.Options);
    }

    protected override ChatDelta? ReadDelta(JsonElement delta)
    {
        if (delta.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var message = JsonNode.Parse(delta.GetRawText());
        NormalizeMessage(message);
        return message.Deserialize<ChatDelta>(LlmJson.Options);
    }

    private static void NormalizeMessage(JsonNode? value)
    {
        if (value is not JsonObject message) throw new ProviderException("invalid_provider_response", false);
        if (message["content"] is JsonArray content)
            message["content"] = string.Concat(content.OfType<JsonObject>()
                .Where(part => part["type"]?.GetValue<string>() == "text").Select(part => part["text"]?.GetValue<string>()));
        if (message["tool_calls"] is JsonArray calls)
            foreach (var call in calls)
                if (call?["function"]?["arguments"] is JsonObject arguments)
                    call["function"]!["arguments"] = arguments.ToJsonString();
    }

    private static void NormalizeToolIds(JsonObject payload)
    {
        var messages = payload["messages"]!.AsArray().OfType<JsonObject>().ToArray();
        var calls = messages.SelectMany(message => message["tool_calls"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        var originals = calls.Select(call => call["id"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToArray();
        static bool Valid(string id) => id.Length == 9 && id.All(char.IsAsciiLetterOrDigit);
        var used = originals.Where(Valid).ToHashSet(StringComparer.Ordinal);
        var mapped = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in originals)
        {
            if (Valid(id)) { mapped[id] = id; continue; }
            // Keep cross-provider tool history usable with Mistral's alphanumeric call IDs.
            var attempt = 0;
            string replacement;
            do { replacement = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id + ":" + attempt++)))[..9]; }
            while (!used.Add(replacement));
            mapped[id] = replacement;
        }
        foreach (var call in calls) call["id"] = mapped[call["id"]!.GetValue<string>()];
        foreach (var message in messages)
            if (message["tool_call_id"]?.GetValue<string>() is { } id && mapped.TryGetValue(id, out var replacement))
                message["tool_call_id"] = replacement;
    }
}
