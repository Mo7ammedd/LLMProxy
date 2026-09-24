import OpenAI from "openai";

const apiKey = process.env.LLMPROXY_API_KEY;
if (!apiKey) throw new Error("Set LLMPROXY_API_KEY to a gateway key.");

const client = new OpenAI({
  baseURL: process.env.LLMPROXY_BASE_URL ?? "http://localhost:4000/v1",
  apiKey,
});
const model = process.env.LLMPROXY_MODEL ?? "fast";
const response = await client.chat.completions.create({
  model,
  messages: [{ role: "user", content: "Hello" }],
});
console.log(response.choices[0]?.message.content);

const stream = await client.chat.completions.create({
  model,
  messages: [{ role: "user", content: "Hello" }],
  stream: true,
  stream_options: { include_usage: true },
});
for await (const chunk of stream) {
  process.stdout.write(chunk.choices[0]?.delta.content ?? "");
}
process.stdout.write("\n");
