using System.Text.Json;
using System.Text.Json.Nodes;
using LLMProxy.Domain;

namespace LLMProxy.Providers;

internal static class MediaContent
{
    public static IEnumerable<JsonObject> Anthropic(ChatMessage message)
    {
        foreach (var part in Parts(message))
        {
            if (part.Text("type") == "text") yield return new JsonObject { ["type"] = "text", ["text"] = part.Text("text") };
            else if (part.Text("type") == "image_url")
            {
                var url = part.Object("image_url").Text("url")!;
                var source = Data(url) is { } data
                    ? new JsonObject { ["type"] = "base64", ["media_type"] = data.Mime, ["data"] = data.Value }
                    : new JsonObject { ["type"] = "url", ["url"] = url };
                yield return new JsonObject { ["type"] = "image", ["source"] = source };
            }
            else ProviderJson.Unsupported("messages.content");
        }
    }

    public static IEnumerable<JsonObject> Gemini(ChatMessage message)
    {
        foreach (var part in Parts(message))
        {
            if (part.Text("type") == "text") yield return new JsonObject { ["text"] = part.Text("text") };
            else if (part.Text("type") == "image_url")
            {
                var url = part.Object("image_url").Text("url")!;
                if (Data(url) is { } data)
                    yield return new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = data.Mime, ["data"] = data.Value } };
                else yield return new JsonObject { ["fileData"] = new JsonObject { ["fileUri"] = url } };
            }
            else if (part.Text("type") == "input_audio")
            {
                var audio = part.Object("input_audio");
                yield return new JsonObject
                {
                    ["inlineData"] = new JsonObject
                    { ["mimeType"] = audio.Text("format") == "wav" ? "audio/wav" : "audio/mpeg", ["data"] = audio.Text("data") }
                };
            }
        }
    }

    private static IEnumerable<JsonElement> Parts(ChatMessage message) => message.Content is { ValueKind: JsonValueKind.Array } content
        ? content.EnumerateArray() : message.Text().Length > 0
            ? [JsonSerializer.SerializeToElement(new { type = "text", text = message.Text() })] : [];
    private static (string Mime, string Value)? Data(string url)
    {
        var separator = url.IndexOf(";base64,", StringComparison.Ordinal);
        return url.StartsWith("data:", StringComparison.Ordinal) && separator > 5 ? (url[5..separator], url[(separator + 8)..]) : null;
    }
}
