using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;
using LLMProxy.Domain;
using Microsoft.Extensions.Configuration;

namespace LLMProxy.Providers;

public static class ProviderAccounts
{
    public static readonly string[] BuiltIns = ["openai", "anthropic", "gemini", "azure", "foundry", "mistral", "cohere", "deepseek", "groq", "ollama"];

    public static IEnumerable<(string Name, string Adapter, ProviderConnectionOptions Connection)> Connections(ProviderOptions options)
    {
        yield return ("openai", "openai", options.OpenAI);
        yield return ("anthropic", "anthropic", options.Anthropic);
        yield return ("gemini", "gemini", options.Gemini);
        yield return ("azure", "azure", options.AzureOpenAI);
        yield return ("foundry", "foundry", options.Foundry);
        yield return ("mistral", "mistral", options.Mistral);
        yield return ("cohere", "cohere", options.Cohere);
        yield return ("deepseek", "deepseek", options.DeepSeek);
        yield return ("groq", "groq", options.Groq);
        yield return ("ollama", "ollama", options.Ollama);
        foreach (var (name, account) in options.Accounts) yield return (name, account.Adapter, account);
    }

    public static string[] Keys(ProviderConnectionOptions connection) => connection.ApiKeys.Length > 0
        ? connection.ApiKeys : string.IsNullOrWhiteSpace(connection.ApiKey) ? [] : [connection.ApiKey];

    public static ProviderOptions Read(IConfiguration configuration)
    {
        var options = configuration.GetSection("LLMProxy:Providers").Get<ProviderOptions>() ?? new();
        var connections = new (string Prefix, ProviderConnectionOptions Connection)[]
        {
            ("OPENAI", options.OpenAI), ("ANTHROPIC", options.Anthropic), ("GEMINI", options.Gemini),
            ("AZURE_OPENAI", options.AzureOpenAI), ("FOUNDRY", options.Foundry), ("MISTRAL", options.Mistral),
            ("COHERE", options.Cohere), ("DEEPSEEK", options.DeepSeek), ("GROQ", options.Groq), ("OLLAMA", options.Ollama)
        };
        foreach (var (prefix, connection) in connections)
        {
            connection.ApiKey = configuration[prefix + "_API_KEY"] ?? connection.ApiKey;
            if (!string.IsNullOrWhiteSpace(configuration[prefix + "_ENDPOINT"])) connection.BaseUrl = configuration[prefix + "_ENDPOINT"]!;
            connection.ValidateApiKeys();
        }
        options.AzureOpenAI.ApiVersion = configuration["AZURE_OPENAI_API_VERSION"] is { Length: > 0 } version ? version : options.AzureOpenAI.ApiVersion;
        options.Foundry.TokenScope = configuration["FOUNDRY_TOKEN_SCOPE"] is { Length: > 0 } scope ? scope : options.Foundry.TokenScope;
        if (configuration["FOUNDRY_AUTHENTICATION"] is { Length: > 0 } authentication)
        {
            if (!Enum.TryParse<FoundryAuthentication>(authentication, true, out var mode) || !Enum.IsDefined(mode))
                throw new InvalidOperationException("FOUNDRY_AUTHENTICATION must be ApiKey or EntraId.");
            options.Foundry.Authentication = mode;
        }
        if (configuration["OLLAMA_ALLOW_INSECURE_HTTP"] is { Length: > 0 } insecure)
        {
            if (!bool.TryParse(insecure, out var allowed)) throw new InvalidOperationException("Invalid Ollama HTTP setting.");
            options.Ollama.AllowInsecureHttp = allowed;
        }
        foreach (var (name, account) in options.Accounts)
        {
            account.ValidateApiKeys();
            if (name.Length is < 1 or > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                || BuiltIns.Contains(name, StringComparer.Ordinal) || !BuiltIns.Contains(account.Adapter, StringComparer.Ordinal)
                || !Enum.IsDefined(account.Authentication) || !Uri.TryCreate(account.BaseUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != "https" && !(account.AllowInsecureHttp && uri.Scheme == "http")
                || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidOperationException("Invalid named provider account.");
        }
        return options;
    }

    public static ILlmProvider Create(string adapter, ProviderOptions options, ProviderHttpTransport transport, TokenCredential? credential) => adapter switch
    {
        "openai" => new OpenAiProvider(transport, options),
        "anthropic" => new AnthropicProvider(transport, options),
        "gemini" => new GeminiProvider(transport, options),
        "azure" => new AzureOpenAiProvider(transport, options),
        "foundry" => new FoundryProvider(transport, options, options.Foundry.Authentication == FoundryAuthentication.EntraId ? credential ?? new DefaultAzureCredential() : null),
        "mistral" => new MistralProvider(transport, options),
        "cohere" => new CohereProvider(transport, options),
        "deepseek" => new DeepSeekProvider(transport, options),
        "groq" => new GroqProvider(transport, options),
        "ollama" => new OllamaProvider(transport, options),
        _ => throw new InvalidOperationException("Unknown provider adapter.")
    };

    public static ILlmProvider CreateAccount(string name, ProviderAccountOptions account, IHttpClientFactory clients, TokenCredential? credential,
        TimeProvider? time = null, IProviderPoolState? poolState = null)
    {
        var options = new ProviderOptions();
        switch (account.Adapter)
        {
            case "openai": options.OpenAI = account; break;
            case "anthropic": options.Anthropic = account; break;
            case "gemini": options.Gemini = account; break;
            case "azure":
                options.AzureOpenAI = new AzureConnectionOptions
                {
                    ApiKey = account.ApiKey,
                    ApiKeys = account.ApiKeys,
                    ApiKeyCooldownSeconds = account.ApiKeyCooldownSeconds,
                    BaseUrl = account.BaseUrl,
                    AllowInsecureHttp = account.AllowInsecureHttp,
                    ApiVersion = account.ApiVersion
                }; break;
            case "foundry":
                options.Foundry = new FoundryConnectionOptions
                {
                    ApiKey = account.ApiKey,
                    ApiKeys = account.ApiKeys,
                    ApiKeyCooldownSeconds = account.ApiKeyCooldownSeconds,
                    BaseUrl = account.BaseUrl,
                    AllowInsecureHttp = account.AllowInsecureHttp,
                    Authentication = account.Authentication,
                    TokenScope = account.TokenScope
                }; break;
            case "mistral": options.Mistral = account; break;
            case "cohere": options.Cohere = account; break;
            case "deepseek": options.DeepSeek = account; break;
            case "groq": options.Groq = account; break;
            case "ollama":
                options.Ollama = new OllamaConnectionOptions
                {
                    ApiKey = account.ApiKey,
                    ApiKeys = account.ApiKeys,
                    ApiKeyCooldownSeconds = account.ApiKeyCooldownSeconds,
                    BaseUrl = account.BaseUrl,
                    AllowInsecureHttp = account.AllowInsecureHttp
                }; break;
        }
        return new NamedProvider(name, Create(account.Adapter, options, new ProviderHttpTransport(new AccountClients(name, clients), time, poolState, name), credential));
    }

    private sealed class AccountClients(string account, IHttpClientFactory inner) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => inner.CreateClient("llmproxy." + account);
    }
    private sealed class NamedProvider(string name, ILlmProvider inner) : ILlmProvider, IProtocolProvider
    {
        public string Name => name;
        public bool IsConfigured => inner.IsConfigured;
        public ModelCapabilities Capabilities => inner.Capabilities;
        public Task<LlmResponse> ChatCompletionAsync(LlmRequest request, CancellationToken ct) => inner.ChatCompletionAsync(request, ct);
        public IAsyncEnumerable<LlmStreamChunk> StreamChatCompletionAsync(LlmRequest request, CancellationToken ct) => inner.StreamChatCompletionAsync(request, ct);
        public Task<JsonObject> CompleteProtocolAsync(GatewayOperation operation, JsonObject payload, CancellationToken ct)
            => ((IProtocolProvider)inner).CompleteProtocolAsync(operation, payload, ct);
        public IAsyncEnumerable<ProtocolEvent> StreamProtocolAsync(GatewayOperation operation, JsonObject payload, CancellationToken ct)
            => ((IProtocolProvider)inner).StreamProtocolAsync(operation, payload, ct);
    }
}
