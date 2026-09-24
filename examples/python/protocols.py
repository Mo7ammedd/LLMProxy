"""Embeddings, stateless Responses and gateway-managed batches using the OpenAI SDK."""
import io
import json
import os
import time
from openai import OpenAI

client = OpenAI(base_url=os.getenv("LLMPROXY_BASE_URL", "http://localhost:4000/v1"),
                api_key=os.environ["LLMPROXY_API_KEY"])
embedding = client.embeddings.create(model="embeddings", input="Hello")
print("Embedding dimensions:", len(embedding.data[0].embedding))
response = client.responses.create(model="fast", input="Hello", store=False)
print(response.output_text)
with client.responses.stream(model="fast", input="Hello", store=False) as stream:
    for event in stream:
        if event.type == "response.output_text.delta":
            print(event.delta, end="", flush=True)
    assert stream.get_final_response().status == "completed"
print()

payload = json.dumps({"custom_id": "example-1", "method": "POST", "url": "/v1/chat/completions",
                      "body": {"model": "fast", "messages": [{"role": "user", "content": "Hello"}]}})
file = client.files.create(file=("requests.jsonl", io.BytesIO((payload + "\n").encode()), "application/jsonl"), purpose="batch")
batch = client.batches.create(input_file_id=file.id, endpoint="/v1/chat/completions", completion_window="24h")
deadline = time.monotonic() + 60
while batch.status in ("validating", "in_progress", "finalizing") and time.monotonic() < deadline:
    time.sleep(.5)
    batch = client.batches.retrieve(batch.id)
if batch.status != "completed" or not batch.output_file_id:
    raise RuntimeError("Batch did not complete successfully: " + batch.status)
output = [json.loads(line) for line in client.files.content(batch.output_file_id).text.splitlines()]
print("Batch completed:", len(output))
