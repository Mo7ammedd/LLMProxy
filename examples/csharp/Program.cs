using System.ClientModel;
using OpenAI;
using OpenAI.Chat;

var key = Environment.GetEnvironmentVariable("LLMPROXY_API_KEY")
    ?? throw new InvalidOperationException("Set LLMPROXY_API_KEY to a gateway key.");
var client = new ChatClient(
    Environment.GetEnvironmentVariable("LLMPROXY_MODEL") ?? "fast",
    new ApiKeyCredential(key),
    new OpenAIClientOptions
    {
        Endpoint = new Uri(Environment.GetEnvironmentVariable("LLMPROXY_BASE_URL") ?? "http://localhost:4000/v1")
    });

ChatMessage[] messages = [new UserChatMessage("Hello")];
ChatCompletion response = await client.CompleteChatAsync(messages);
foreach (var part in response.Content) Console.Write(part.Text);
Console.WriteLine();

await foreach (var update in client.CompleteChatStreamingAsync(messages))
    foreach (var part in update.ContentUpdate) Console.Write(part.Text);
Console.WriteLine();
