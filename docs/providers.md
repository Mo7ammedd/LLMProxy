# Provider setup

Applications keep the same `/v1` base URL, gateway key and public model alias for every provider. Configure upstream credentials and model/deployment mappings on the gateway. Adapters handle HTTP authentication, text, function tools, SSE framing differences, token usage and finish reasons. Provider capabilities still vary; see the [API compatibility matrix](api.md#compatibility).

## Default models

The built-in `fast` alias uses the following priority order. Providers participate only when configured; adding a credential enables that provider's configured mappings. Change the order or strategy to choose a different primary provider.

| Registry ID | `fast` upstream model | `reasoning` upstream model |
| --- | --- | --- |
| `openai` | `gpt-4o-mini` | `gpt-5` |
| `anthropic` | `claude-haiku-4-5-20251001` | `claude-sonnet-4-6` |
| `gemini` | `gemini-2.5-flash` | — |
| `azure` | Deployment `gpt-4o-mini` | — |
| `foundry` | Deployment `gpt-4o-mini` | Deployment `gpt-5` |
| `mistral` | `mistral-small-latest` | — |
| `cohere` | `command-r7b-12-2024` | — |
| `deepseek` | `deepseek-flash` | `deepseek-v4-pro` |
| `groq` | `llama-3.1-8b-instant` | — |
| `ollama` | `llama3.1:8b` | — |

These are editable defaults, not a guarantee of model availability in your account. Readiness checks configuration without invoking a model or testing credentials remotely. Change mappings, capability declarations and prices for the models you operate. [Named accounts](configuration.md#named-provider-accounts) allow separate endpoints/credentials per adapter. They have separate circuits and concurrency limits and can be reloaded with model/pricing configuration.

Every API-key adapter and named account also supports an [`ApiKeys` pool](configuration.md#multiple-api-keys) under one provider name. Requests rotate across keys, with automatic key failover and cooldowns. The pool shares model mappings, pricing and the provider concurrency limit; attempts identify the selected key with a fingerprint. Existing single-key settings remain supported.

The `embeddings` alias maps OpenAI to `text-embedding-3-small`, Mistral to `mistral-embed` and Ollama to `nomic-embed-text`. Its fallback is disabled: choose a fixed model for each vector index. Adapter-level embeddings support also includes Azure OpenAI and Foundry. Native stateless Responses is supported through OpenAI and Foundry. [Extended protocol details](expanded-api.md#embeddings).

## Microsoft Foundry

The `foundry` adapter uses Microsoft's OpenAI v1 chat, embeddings and Responses endpoints. Accepted base URL forms include:

```text
https://RESOURCE.services.ai.azure.com
https://RESOURCE.services.ai.azure.com/openai/v1/
https://RESOURCE.openai.azure.com/openai/v1/
```

The resource root is expanded to `/openai/v1/chat/completions`. Full v1 URLs are not duplicated. This adapter does not use the legacy `api-version` query parameter.

For API-key authentication, put these settings in your deployment environment:

```dotenv
FOUNDRY_ENDPOINT=https://RESOURCE.services.ai.azure.com/openai/v1/
FOUNDRY_AUTHENTICATION=ApiKey
FOUNDRY_API_KEY=your-foundry-resource-key
```

For Microsoft Entra authentication:

```dotenv
FOUNDRY_ENDPOINT=https://RESOURCE.services.ai.azure.com/openai/v1/
FOUNDRY_AUTHENTICATION=EntraId
FOUNDRY_API_KEY=
FOUNDRY_TOKEN_SCOPE=https://ai.azure.com/.default
```

`DefaultAzureCredential` acquires and refreshes tokens. In Azure, assign a managed identity and grant it inference access on the resource. For an OpenAI deployment, `Cognitive Services OpenAI User` is a common inference role; use the role required by your resource and model. `AZURE_CLIENT_ID` can select a user-assigned identity. A service principal can use `AZURE_TENANT_ID`, `AZURE_CLIENT_ID` and `AZURE_CLIENT_SECRET`, which Compose forwards. Workload identity also needs a projected token file and `AZURE_FEDERATED_TOKEN_FILE`; supply the mount/environment setting in your platform or Compose override. Local developers can use credentials supported by Azure.Identity, such as an Azure CLI login.

Change `FOUNDRY_TOKEN_SCOPE` if your resource or cloud requires a different audience. Credential failures return a sanitized `provider_authentication_failed` error. Automated tests inject a fake `TokenCredential`; they never acquire real Entra tokens.

**Model mappings are deployment names.** If your deployment is named `chat-mini`, change the existing `fast` mapping and add its corresponding price in your full configuration:

```json
{
  "LLMProxy": {
    "Models": {
      "fast": { "ProviderModels": { "foundry": "chat-mini" } }
    },
    "Pricing": {
      "foundry/chat-mini": { "InputPerMillion": 0.15, "OutputPerMillion": 0.60 }
    }
  }
}
```

This is an excerpt; retain the other required model/pricing entries. Replace the illustrative price with your deployment's rate. For a deployment serving only Foundry, use a complete configuration with `Providers: ["foundry"]`; see [replacing configuration arrays](configuration.md#complete-json-replacement).

Coverage is limited to **OpenAI v1 inference deployments**. Responses uses the gateway's stateless subset; embeddings requires a compatible deployment mapping. Foundry project URLs (`/api/projects/...`), Agents, the legacy Azure AI Inference preview API and Foundry's separate Anthropic/Claude API are not handled. Tools, media, reasoning controls and sampling parameters depend on the deployed model. The separate `azure` adapter supports chat and embeddings through legacy Azure OpenAI deployment endpoints.

References: [Microsoft's v1 API guidance](https://learn.microsoft.com/en-us/azure/foundry/openai/api-version-lifecycle) · [Foundry SDK and authentication guidance](https://learn.microsoft.com/en-us/azure/foundry/how-to/develop/sdk-overview).

## Mistral

Set `MISTRAL_API_KEY`. The default endpoint is `https://api.mistral.ai/v1`; `MISTRAL_ENDPOINT` overrides it.

The adapter maps output limits to `max_tokens` and `seed` to `random_seed`. It consumes Mistral's native stream usage without sending OpenAI-only stream options. Native content blocks and object-valued function arguments are normalized to OpenAI text/argument strings. Historical tool-call IDs from other providers are mapped consistently to nine alphanumeric characters in both assistant calls and tool results; the client's original history is not modified.

The default is `mistral-small-latest`. Pin an upstream model version if you need a stable release, and update pricing when changing the mapping. [Chat API reference](https://docs.mistral.ai/api/endpoint/chat) · [Mistral pricing](https://mistral.ai/pricing).

## Cohere

Set `COHERE_API_KEY`. The adapter calls `https://api.cohere.com/v2/chat`; `COHERE_ENDPOINT` overrides the `/v2` base URL. It translates Cohere's native normal responses and `message-start`, `content-delta`, tool-call and `message-end` SSE events.

The default `command-r7b-12-2024` supports text and tools. `tool_choice` is translated to Cohere's `REQUIRED`/`NONE`; a named function restricts the offered tool list. Per-function strict settings must agree because Cohere applies strictness to the whole selected tool list. OpenAI JSON schema output is translated to Cohere's `json_object` plus `json_schema` format. JSON output cannot be combined with tools, and disabling parallel tools is unsupported.

`top_p` becomes Cohere's `p` and must be between 0.01 and 0.99. The gateway returns actual `usage.tokens` when available, then falls back to `billed_units`. Cost estimates can therefore include tokens Cohere does not bill. Retrieval documents, citations, thinking controls and tool plans are outside the current gateway contract. [Cohere Chat API](https://docs.cohere.com/v2/reference/chat) · [Streaming reference](https://docs.cohere.com/v2/reference/chat-stream) · [Pricing](https://cohere.com/pricing).

## DeepSeek

Set `DEEPSEEK_API_KEY`. The default endpoint is `https://api.deepseek.com/v1`; `DEEPSEEK_ENDPOINT` overrides it. Defaults map `fast` to `deepseek-flash` and `reasoning` to `deepseek-v4-pro`.

The adapter sends `max_tokens`, collects usage from the final content chunk and preserves the optional `reasoning_content` field separately from visible `content`. Keep complete assistant messages, including reasoning and tool calls, when sending subsequent tool results for thinking models. Unknown-field preservation depends on your SDK. Reasoning history is omitted when forwarding the request to another provider.

Thinking defaults are controlled by the upstream model. This gateway does not yet expose provider-specific thinking controls or `reasoning_effort`. Current thinking models may reject forced/named tool choices; those rejections return `provider_rejected_request`. Seed, strict beta tools, JSON schema output and disabling parallel tool calls are unsupported. JSON object output and ordinary tool selection are supported. Allocate enough output tokens for the reasoning and final answer together.

Default prices use peak uncached rates, excluding cache/off-peak discounts. [Chat API](https://api-docs.deepseek.com/api/create-chat-completion/) · [Thinking and tool history](https://api-docs.deepseek.com/guides/thinking_mode) · [Models and pricing](https://api-docs.deepseek.com/quick_start/pricing).

## Groq

Set `GROQ_API_KEY`. The default endpoint is `https://api.groq.com/openai/v1`; `GROQ_ENDPOINT` overrides it. The default model is `llama-3.1-8b-instant`.

The adapter uses `max_completion_tokens`, maps developer instructions to system messages and reads both ordinary `usage` and final `x_groq.usage`. It does not send `stream_options`. Message names are rejected; function tools, seed and response formats are forwarded and remain subject to the selected model's capabilities. [OpenAI compatibility](https://console.groq.com/docs/openai) · [Pricing](https://groq.com/pricing).

## Ollama

Start Ollama and install the configured model:

```bash
ollama pull llama3.1:8b
```

For a gateway running from source on the same machine:

```dotenv
OLLAMA_ENDPOINT=http://localhost:11434/v1
OLLAMA_ALLOW_INSECURE_HTTP=true
```

For Docker/Compose connecting to Ollama on the host:

```dotenv
OLLAMA_ENDPOINT=http://host.docker.internal:11434/v1
OLLAMA_ALLOW_INSECURE_HTTP=true
```

The supplied Compose file adds the host-gateway mapping. With plain Docker on Linux, add `--add-host=host.docker.internal:host-gateway` to the gateway's `docker run` command. `localhost` inside the gateway container refers to that container. Ollama must listen on an interface reachable from the gateway; restrict its port to the trusted network. Containers on the same private Docker network can instead use the Ollama service name.

Ollama is disabled until an explicit endpoint is configured. Local Ollama needs no provider API key. `OLLAMA_API_KEY` is optional for an authenticated reverse proxy. HTTP requires explicit opt-in; use HTTPS for remote deployments.

The adapter supports text, tools, streaming, embeddings, and model-supported image input, seed and JSON formats. The default `llama3.1:8b` target does not declare image capability; use a compatible installed model and update its target capabilities. `tool_choice: "auto"` uses native selection; `"none"` removes offered tools. Required/named selection, strict tools and disabling parallel calls are excluded by capability routing. Direct adapter calls can return `unsupported_parameter`.

The upstream model name remains `llama3.1:8b`, while its pricing key is `ollama/llama3.1%3A8b` because colons separate .NET configuration paths. Default token cost is zero; hardware, electricity and hosting costs are not included. Change both model mapping and pricing when selecting a different installed model. [Ollama OpenAI compatibility](https://docs.ollama.com/api/openai-compatibility).

## Testing an adapter without provider access

```bash
dotnet test tests/LLMProxy.ProviderTests --configuration Release
dotnet build --configuration Release
python scripts/source_smoke.py --provider cohere
```

Install the example SDK dependencies first as described in the README. The source smoke command supports every added provider. Docker smoke tests run the same Python, TypeScript and C# SDK clients for all six adapters against a local fixture. External providers and identity services are never contacted by automated tests.
