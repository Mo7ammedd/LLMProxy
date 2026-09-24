"""Local, deterministic provider wire-protocol fixtures. Never contacts a provider."""
import json
import argparse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *_args):
        pass

    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b'{"status":"ok"}')

    def do_POST(self):
        if self.path not in ("/v1/chat/completions", "/openai/v1/chat/completions", "/v2/chat"):
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
