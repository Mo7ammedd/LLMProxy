"""Local, deterministic OpenAI wire-protocol fixture. Never contacts a provider."""
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
        if streaming:
            chunks = [
                {"choices": [{"index": 0, "delta": {"role": "assistant", "content": "Hello"}, "finish_reason": None}]},
                {"choices": [{"index": 0, "delta": {}, "finish_reason": "stop"}]},
                {"choices": [], "usage": usage},
            ]
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
