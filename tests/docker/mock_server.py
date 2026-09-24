"""Local, deterministic provider wire-protocol fixtures. Never contacts a provider."""
import json
import argparse
import base64
import struct
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    long_stream_seconds = 0
    def log_message(self, *_args):
        pass

    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b'{"data":[{"id":"fixture-model"}]}' if self.path.endswith('/models') else b'{"status":"ok"}')

    def do_POST(self):
        if self.path not in ("/v1/chat/completions", "/openai/v1/chat/completions", "/v2/chat",
                             "/v1/embeddings", "/openai/v1/embeddings", "/v1/responses", "/openai/v1/responses"):
            self.send_error(404)
            return
        size = int(self.headers.get("Content-Length", "0"))
        if size > 1048576:
            self.send_error(413)
            return
        request = json.loads(self.rfile.read(size))
        streaming = request.get("stream", False)
        usage = {"prompt_tokens": 3, "completion_tokens": 2, "total_tokens": 5}
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream" if streaming else "application/json")
        self.end_headers()
        if self.path.endswith("/embeddings"):
            embedding = base64.b64encode(struct.pack("<2f", .1, .2)).decode() if request.get("encoding_format") == "base64" else [.1, .2]
            self.wfile.write(json.dumps({"object": "list", "model": request["model"],
                "data": [{"object": "embedding", "index": 0, "embedding": embedding}],
                "usage": {"prompt_tokens": 3, "total_tokens": 3}}).encode())
            return
        if self.path.endswith("/responses"):
            content = {"type": "output_text", "text": "Hello", "annotations": [], "logprobs": []}
            item = {"id": "msg_mock", "type": "message", "role": "assistant", "status": "completed", "content": [content]}
            result = {"id": "resp_mock", "object": "response", "created_at": 1, "status": "completed",
                      "model": request["model"], "output": [item], "error": None, "incomplete_details": None,
                      "metadata": {}, "tools": [], "tool_choice": "auto", "parallel_tool_calls": True,
                      "usage": {"input_tokens": 3, "output_tokens": 2, "total_tokens": 5,
                                "input_tokens_details": {"cached_tokens": 0}, "output_tokens_details": {"reasoning_tokens": 0}}}
            if not streaming:
                self.wfile.write(json.dumps(result).encode())
                return
            events = [
                {"type": "response.created", "response": {**result, "status": "in_progress", "output": [], "usage": None}},
                {"type": "response.output_item.added", "output_index": 0, "item": {**item, "status": "in_progress", "content": []}},
                {"type": "response.content_part.added", "item_id": "msg_mock", "output_index": 0, "content_index": 0, "part": {**content, "text": ""}},
                {"type": "response.output_text.delta", "item_id": "msg_mock", "output_index": 0, "content_index": 0, "delta": "Hello", "logprobs": []},
                {"type": "response.output_text.done", "item_id": "msg_mock", "output_index": 0, "content_index": 0, "text": "Hello", "logprobs": []},
                {"type": "response.content_part.done", "item_id": "msg_mock", "output_index": 0, "content_index": 0, "part": content},
                {"type": "response.output_item.done", "output_index": 0, "item": item},
                {"type": "response.completed", "response": result},
            ]
            for index, event in enumerate(events):
                event["sequence_number"] = index
                self.wfile.write(("event: " + event["type"] + "\ndata: " + json.dumps(event) + "\n\n").encode())
                self.wfile.flush()
            return
        if self.path == "/v2/chat":
            native_usage = {"tokens": {"input_tokens": 3, "output_tokens": 2},
                            "billed_units": {"input_tokens": 1, "output_tokens": 1}}
            if streaming:
                events = [
                    {"type": "message-start", "delta": {"message": {"role": "assistant"}}},
                    {"type": "content-delta", "delta": {"message": {"content": {"type": "text", "text": "Hello"}}}},
                    {"type": "message-end", "delta": {"finish_reason": "COMPLETE", "usage": native_usage}},
                ]
                for event in events:
                    self.wfile.write(("event: " + event["type"] + "\ndata: " + json.dumps(event) + "\n\n").encode())
                    self.wfile.flush()
            else:
                self.wfile.write(json.dumps({"id": "cohere-mock", "finish_reason": "COMPLETE",
                    "message": {"role": "assistant", "content": [{"type": "text", "text": "Hello"}]},
                    "usage": native_usage}).encode())
            return
        if streaming:
            if self.long_stream_seconds and any(message.get("content") == "__long_stream__" for message in request.get("messages", [])):
                try:
                    for _ in range(max(1, int(self.long_stream_seconds * 10))):
                        self.wfile.write(b'data: {"choices":[{"index":0,"delta":{"content":"Hello"}}]}\n\n')
                        self.wfile.flush()
                        time.sleep(.1)
                except (BrokenPipeError, ConnectionResetError):
                    return
            chunks = [
                {"choices": [{"index": 0, "delta": {"role": "assistant", "content": "Hello"}, "finish_reason": None}]},
                {"choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}]},
                {"choices": [], "usage": usage},
            ]
            if request["model"] == "llama-3.1-8b-instant":
                chunks = chunks[:2]
                chunks[-1]["x_groq"] = {"usage": usage}
            elif request["model"] == "deepseek-flash":
                chunks = chunks[:2]
                chunks[-1]["usage"] = usage
            elif request["model"] == "mistral-small-latest":
                chunks[0]["choices"][0]["delta"]["content"] = [{"type": "text", "text": "Hello"}]
            for chunk in chunks:
                self.wfile.write(("data: " + json.dumps(chunk) + "\n\n").encode())
                self.wfile.flush()
            self.wfile.write(b"data: [DONE]\n\n")
        else:
            self.wfile.write(json.dumps({
                "id": "chatcmpl-mock", "object": "chat.completion", "created": 1,
                "model": request["model"], "choices": [{"index": 0,
                    "message": {"role": "assistant", "content": "Hello"}, "finish_reason": "stop"}],
                "usage": usage,
            }).encode())


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=9000)
    parser.add_argument("--host", default="0.0.0.0")
    args = parser.parse_args()
    ThreadingHTTPServer((args.host, args.port), Handler).serve_forever()
