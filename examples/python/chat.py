"""Run with LLMPROXY_API_KEY and optionally LLMPROXY_BASE_URL / LLMPROXY_MODEL."""
import os
from openai import OpenAI

client = OpenAI(
    base_url=os.getenv("LLMPROXY_BASE_URL", "http://localhost:4000/v1"),
    api_key=os.environ["LLMPROXY_API_KEY"],
)
model = os.getenv("LLMPROXY_MODEL", "fast")
messages = [{"role": "user", "content": "Hello"}]

response = client.chat.completions.create(model=model, messages=messages)
print(response.choices[0].message.content)

stream = client.chat.completions.create(
    model=model, messages=messages, stream=True, stream_options={"include_usage": True}
)
for chunk in stream:
    if chunk.choices:
        print(chunk.choices[0].delta.content or "", end="", flush=True)
print()
