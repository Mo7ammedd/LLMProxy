"""Black-box checks shared by source and container smoke tests."""
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
KEY = "llmp_sk_docker_fixture_only_not_a_real_api_key_123456"
ADMIN_KEY = "admin_docker_fixture_only_not_a_real_key_123456"
PROVIDER_NAMES = ("openai", "foundry", "mistral", "cohere", "deepseek", "groq", "ollama")


def isolated_environment():
    """Do not inherit real provider credentials, endpoints or identity settings."""
    prefixes = ("OPENAI_", "ANTHROPIC_", "GEMINI_", "AZURE_", "FOUNDRY_", "MISTRAL_",
                "COHERE_", "DEEPSEEK_", "GROQ_", "OLLAMA_", "LLMPROXY__PROVIDERS__")
    return {key: value for key, value in os.environ.items() if not key.upper().startswith(prefixes)}


def provider_environment(provider, mock_base):
    section, path = {
        "openai": ("OpenAI", "/v1"), "foundry": ("Foundry", ""), "mistral": ("Mistral", "/v1"),
        "cohere": ("Cohere", "/v2"), "deepseek": ("DeepSeek", "/v1"),
        "groq": ("Groq", "/openai/v1"), "ollama": ("Ollama", "/v1"),
    }[provider]
    settings = {
        provider.upper() + "_ENDPOINT": mock_base + path,
        "LLMProxy__Providers__" + section + "__AllowInsecureHttp": "true",
    }
    if provider != "ollama":
        settings[provider.upper() + "_API_KEY"] = "mock-only-not-a-live-key"
    if provider == "foundry":
        settings["FOUNDRY_AUTHENTICATION"] = "ApiKey"
    return settings


def request(base, path, data=None, key=KEY):
    headers = {"Authorization": f"Bearer {key}"}
    if data is not None:
        headers["Content-Type"] = "application/json"
    req = urllib.request.Request(base + path, data=None if data is None else json.dumps(data).encode(), headers=headers)
    return urllib.request.urlopen(req, timeout=15)


def wait_ready(base, seconds=90):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(base + "/health/ready", timeout=3) as response:
                if response.status == 200:
                    return
        except (urllib.error.URLError, TimeoutError, ConnectionError):
            # A restarting container can accept TCP before its HTTP listener is ready.
            pass
        time.sleep(0.5)
    raise AssertionError("Gateway did not become ready before the smoke-test deadline.")


def check_http(base):
    with request(base, "/v1/models") as response:
        models = json.load(response)
        assert "fast" in [item["id"] for item in models["data"]]
    payload = {"model": "fast", "messages": [{"role": "user", "content": "Hello"}]}
    with request(base, "/v1/chat/completions", payload) as response:
        body = json.load(response)
        assert response.headers["X-Request-Id"]
        assert body["object"] == "chat.completion"
        assert body["model"] == "fast"
        assert body["choices"][0]["message"]["content"] == "Hello"
        assert body["usage"]["total_tokens"] == 5
    payload.update(stream=True, stream_options={"include_usage": True})
    with request(base, "/v1/chat/completions", payload) as response:
        assert response.headers["Content-Type"].startswith("text/event-stream")
        frames = [line[6:] for line in response.read().decode().splitlines() if line.startswith("data: ")]
        assert frames[-1] == "[DONE]"
        chunks = [json.loads(frame) for frame in frames[:-1]]
        assert len({chunk["id"] for chunk in chunks}) == 1
        assert chunks[-1]["choices"] == []
        assert chunks[-1]["usage"]["total_tokens"] == 5
    try:
        request(base, "/v1/models", key="wrong-key")
        raise AssertionError("Invalid API key was accepted.")
    except urllib.error.HTTPError as error:
        assert error.code == 401
        assert json.load(error)["error"]["code"] == "invalid_api_key"
    try:
        request(base, "/v1/chat/completions", {
            "model": "fast", "messages": [{"role": "user", "content": "x" * 1048576}]
        })
        raise AssertionError("An oversized request was accepted.")
    except urllib.error.HTTPError as error:
        assert error.code == 413
        assert "error" in json.load(error)
    print("HTTP, SSE, authentication and model compatibility passed.", flush=True)


def check_sdks(base, configuration="Release"):
    environment = isolated_environment()
    environment.update(LLMPROXY_BASE_URL=base + "/v1", LLMPROXY_API_KEY=KEY)
    commands = [
        [sys.executable, "examples/python/chat.py"],
        ["node", "examples/typescript/chat.ts"],
        ["dotnet", "run", "--project", "examples/csharp", "--configuration", configuration, "--no-build", "--no-restore"],
    ]
    for name, command in zip(["Python", "TypeScript", "C#"], commands):
        result = subprocess.run(command, cwd=ROOT, env=environment, capture_output=True, text=True, check=True, timeout=60)
        if result.stdout.strip().splitlines() != ["Hello", "Hello"]:
            raise AssertionError(f"{name} SDK returned unexpected content: {result.stdout}")
        print(f"{name} SDK: normal and streaming completions passed.", flush=True)


def check_persisted_usage(base):
    with request(base, "/admin/usage?limit=100", key=ADMIN_KEY) as response:
        usage = json.load(response)
    assert len(usage) >= 2
    assert all(row["total_tokens"] == 5 and row["status"] == "success" for row in usage)
    return len(usage)


def check_protocol_sdks(base):
    environment = isolated_environment()
    environment.update(LLMPROXY_BASE_URL=base + "/v1", LLMPROXY_API_KEY=KEY)
    for name, command, expected in [
        ("Python", [sys.executable, "examples/python/protocols.py"], ["Embedding dimensions: 2", "Hello", "Hello", "Batch completed: 1"]),
        ("TypeScript", ["node", "examples/typescript/protocols.ts"], ["Embedding dimensions: 2", "Hello", "Hello"]),
    ]:
        result = subprocess.run(command, cwd=ROOT, env=environment, capture_output=True, text=True, timeout=90)
        if result.returncode or result.stdout.strip().splitlines() != expected:
            raise AssertionError(f"{name} protocol SDK check failed: {result.stderr[-4000:]}")
        print(f"{name} SDK: embeddings and Responses passed" + ("; files and batches passed." if name == "Python" else "."), flush=True)
