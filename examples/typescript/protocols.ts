import OpenAI from "openai";

const client = new OpenAI({
  baseURL: process.env.LLMPROXY_BASE_URL ?? "http://localhost:4000/v1",
  apiKey: process.env.LLMPROXY_API_KEY,
});
const embedding = await client.embeddings.create({model: "embeddings", input: "Hello"});
console.log("Embedding dimensions:", embedding.data[0].embedding.length);
const response = await client.responses.create({model: "fast", input: "Hello", store: false});
console.log(response.output_text);
const stream = await client.responses.create({model: "fast", input: "Hello", stream: true, store: false});
let completed = false;
for await (const event of stream) {
  if (event.type === "response.output_text.delta") process.stdout.write(event.delta);
  if (event.type === "response.completed") completed = true;
}
if (!completed) throw new Error("Response stream did not complete.");
process.stdout.write("\n");
