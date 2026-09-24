using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

public sealed class GroqProvider(ProviderHttpTransport transport, ProviderOptions options)
    : OpenAiCompatibleProvider(transport, options.Groq)
{
    public override string Name => "groq";
    protected override bool SupportsDeveloperRole => false;
    // Groq includes usage in its final x_groq event without requesting stream_options.
    protected override bool RequestStreamUsage => false;
    protected override TokenUsage? ReadUsage(JsonElement root) => base.ReadUsage(root) ?? base.ReadUsage(root.Object("x_groq"));

    protected override JsonObject BuildPayload(LlmRequest request)
    {
        if (request.Messages.Any(message => message.Name is not null)) ProviderJson.Unsupported("messages.name");
        return base.BuildPayload(request);
    }
}
