using LLMProxy.Domain;

namespace LLMProxy.Providers;

public static class ProviderCapabilities
{
    public static ModelCapabilities For(string adapter) => adapter switch
    {
        "anthropic" => new()
        {
            Features = ModelCapability.Chat | ModelCapability.Tools | ModelCapability.RequiredTool
            | ModelCapability.NamedTool | ModelCapability.DisableParallelTools | ModelCapability.ImageInput | ModelCapability.ThinkingBudget,
            MaxTemperature = 1,
            AcceptsResponseFormat = false,
            ThinkingWithTools = false
        },
        "gemini" => new()
        {
            Features = ModelCapability.Chat | ModelCapability.Tools | ModelCapability.JsonObject
            | ModelCapability.JsonSchema | ModelCapability.RequiredTool | ModelCapability.NamedTool | ModelCapability.ImageInput
            | ModelCapability.AudioInput | ModelCapability.ThinkingBudget
        },
        "cohere" => new()
        {
            Features = ModelCapability.TextChat & ~(ModelCapability.DisableParallelTools | ModelCapability.MessageNames),
            MinTopP = .01,
            MaxTopP = .99,
            JsonWithTools = false,
            MixedToolStrictness = false
        },
        "deepseek" => new()
        {
            Features = ModelCapability.Chat | ModelCapability.Tools | ModelCapability.JsonObject
            | ModelCapability.RequiredTool | ModelCapability.NamedTool
        },
        "ollama" => new()
        {
            Features = ModelCapability.Chat | ModelCapability.Tools | ModelCapability.JsonObject
            | ModelCapability.JsonSchema | ModelCapability.Seed | ModelCapability.ImageInput | ModelCapability.Embeddings
        },
        "mistral" => new() { Features = ModelCapability.TextChat | ModelCapability.ImageInput | ModelCapability.Embeddings, ToolMessageNamesOnly = true },
        "groq" => new()
        {
            Features = (ModelCapability.TextChat & ~ModelCapability.MessageNames)
            | ModelCapability.ImageInput | ModelCapability.ReasoningEffort
        },
        "azure" => new()
        {
            Features = ModelCapability.TextChat | ModelCapability.ImageInput | ModelCapability.AudioInput
            | ModelCapability.ReasoningEffort | ModelCapability.Embeddings
        },
        _ => new()
        {
            Features = ModelCapability.TextChat | ModelCapability.ImageInput | ModelCapability.AudioInput
            | ModelCapability.ReasoningEffort | ModelCapability.Embeddings | ModelCapability.Responses
        }
    };
}
