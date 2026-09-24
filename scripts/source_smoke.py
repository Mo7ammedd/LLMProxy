"""Run the built source server against a local mock, including three unmodified SDKs."""
import argparse
import os
import socket
import subprocess
import sys
import tempfile
from pathlib import Path
from smoke_common import ADMIN_KEY, KEY, ROOT, check_http, check_persisted_usage, check_sdks, wait_ready


def free_port():
    with socket.socket() as listener:
        listener.bind(("127.0.0.1", 0))
        return listener.getsockname()[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--configuration", default="Release")
    args = parser.parse_args()
    port, mock_port = free_port(), free_port()
    base = f"http://127.0.0.1:{port}"
    with tempfile.TemporaryDirectory(prefix="llmproxy-smoke-") as temporary:
        environment = os.environ.copy()
        environment.update({
            "ASPNETCORE_URLS": base, "ASPNETCORE_HTTP_PORTS": "", "ASPNETCORE_HTTPS_PORTS": "",
            "LLMProxy__Storage__Mode": "Standalone", "LLMProxy__Storage__SqlitePath": str(Path(temporary) / "gateway.db"),
            "LLMProxy__Storage__AutoMigrate": "true",
            "LLMProxy__Providers__OpenAI__BaseUrl": f"http://127.0.0.1:{mock_port}/v1",
            "LLMProxy__Providers__OpenAI__AllowInsecureHttp": "true",
            "OPENAI_API_KEY": "mock-only-not-a-live-key", "ANTHROPIC_API_KEY": "", "GEMINI_API_KEY": "", "AZURE_OPENAI_API_KEY": "",
            "LLMPROXY_BOOTSTRAP_KEY": KEY, "LLMPROXY_ADMIN_KEY": ADMIN_KEY,
            "OTEL_EXPORTER_OTLP_ENDPOINT": "",
        })
        log_path = Path(temporary) / "server.log"
        processes = []
        with log_path.open("w") as log:
            try:
                processes.append(subprocess.Popen([sys.executable, str(ROOT / "tests/docker/mock_server.py"), "--host", "127.0.0.1", "--port", str(mock_port)], stdout=log, stderr=log))
                server = ROOT / f"src/LLMProxy.Server/bin/{args.configuration}/net10.0/LLMProxy.Server.dll"
                processes.append(subprocess.Popen(["dotnet", str(server)], cwd=ROOT / "src/LLMProxy.Server", env=environment, stdout=log, stderr=log))
                wait_ready(base)
                check_http(base)
                check_sdks(base, args.configuration)
                check_persisted_usage(base)
                print("Source smoke test passed; no external LLM provider was contacted.", flush=True)
            except Exception:
                log.flush()
                print(log_path.read_text()[-12000:], file=sys.stderr)
                raise
            finally:
                for process in reversed(processes):
                    process.terminate()
                    try:
                        process.wait(timeout=50)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()


if __name__ == "__main__":
    main()
