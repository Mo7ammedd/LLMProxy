"""Verify the production image with PostgreSQL, Redis, and a mock provider."""
import json
import os
import subprocess
import urllib.error
import uuid
from smoke_common import ADMIN_KEY, KEY, ROOT, check_http, check_persisted_usage, check_sdks, request, wait_ready


def main():
    project = "llmproxy-smoke-" + uuid.uuid4().hex[:8]
    command = ["docker", "compose", "--project-name", project, "--env-file", "tests/docker/test.env",
               "-f", "docker-compose.yml", "-f", "tests/docker/docker-compose.test.yml"]
    environment = os.environ.copy()
    # Parent-shell provider credentials must never override the fake test environment.
    for line in (ROOT / "tests/docker/test.env").read_text().splitlines():
        if line and not line.startswith("#"):
            name, value = line.split("=", 1)
            environment[name] = value
    environment.update(ANTHROPIC_API_KEY="", GEMINI_API_KEY="", AZURE_OPENAI_API_KEY="", OTEL_EXPORTER_OTLP_ENDPOINT="")
    port = environment.get("LLMPROXY_PORT", "14000")
    base = f"http://127.0.0.1:{port}"
    standalone = project + "-standalone"

    def compose(*args, **kwargs):
        return subprocess.run(command + list(args), cwd=ROOT, env=environment, check=True, **kwargs)

    try:
        compose("up", "--detach", "--wait", "--wait-timeout", "120")
        wait_ready(base)
        check_http(base)
        check_sdks(base)
        count = check_persisted_usage(base)
        container = compose("ps", "--quiet", "llmproxy", capture_output=True, text=True).stdout.strip()
        inspection = json.loads(subprocess.check_output(["docker", "inspect", container], text=True))[0]
        assert inspection["Config"]["User"] not in ("", "0", "root", "0:0")
        result = subprocess.run(["docker", "exec", container, "dotnet", "LLMProxy.Server.dll", "keys", "create",
                                 "--owner", "docker-cli", "--models", "fast", "--rpm", "1"],
                                capture_output=True, text=True, check=True)
        limited = json.loads(result.stdout)["key"]
        with request(base, "/v1/models", key=limited) as response:
            assert response.status == 200
        with request(base, "/admin/keys", key=ADMIN_KEY) as response:
            bootstrap_id = next(key["id"] for key in json.load(response) if key["prefix"] == KEY[:16])
        subprocess.run(["docker", "exec", container, "dotnet", "LLMProxy.Server.dll", "keys", "disable", bootstrap_id],
                       check=True, capture_output=True, text=True)
        compose("restart", "llmproxy")
        wait_ready(base)
        assert check_persisted_usage(base) == count
        try:
            request(base, "/v1/models", key=limited)
            raise AssertionError("Redis rate limit was reset by a gateway restart.")
        except urllib.error.HTTPError as error:
            assert error.code == 429
        try:
            request(base, "/v1/models")
            raise AssertionError("A disabled bootstrap key was re-enabled by restart.")
        except urllib.error.HTTPError as error:
            assert error.code == 401

        # Exercise the primary one-container experience and an alternate internal listening port.
        subprocess.run(["docker", "run", "--detach", "--name", standalone, "--network", project + "_gateway",
                        "--read-only", "--tmpfs", "/tmp", "--publish", "127.0.0.1::8080",
                        "--env", "ASPNETCORE_HTTP_PORTS=8080", "--env", "OPENAI_API_KEY=mock-only-not-a-live-key",
                        "--env", "LLMPROXY_BOOTSTRAP_KEY=" + KEY, "--env", "LLMPROXY_ADMIN_KEY=" + ADMIN_KEY,
                        "--env", "LLMProxy__Providers__OpenAI__BaseUrl=http://mock:9000/v1",
                        "--env", "LLMProxy__Providers__OpenAI__AllowInsecureHttp=true",
                        "llmproxy:smoke"], check=True, capture_output=True, text=True)
        inspection = json.loads(subprocess.check_output(["docker", "inspect", standalone], text=True))[0]
        standalone_port = inspection["NetworkSettings"]["Ports"]["8080/tcp"][0]["HostPort"]
        standalone_url = f"http://127.0.0.1:{standalone_port}"
        wait_ready(standalone_url)
        check_http(standalone_url)
        standalone_count = check_persisted_usage(standalone_url)
        subprocess.run(["docker", "restart", standalone], check=True, capture_output=True)
        wait_ready(standalone_url)
        assert check_persisted_usage(standalone_url) == standalone_count
        subprocess.run(["docker", "exec", standalone, "dotnet", "LLMProxy.Server.dll", "healthcheck"], check=True)
        compose("stop", "--timeout", "60", "llmproxy")
        inspection = json.loads(subprocess.check_output(["docker", "inspect", container], text=True))[0]
        assert inspection["State"]["ExitCode"] == 0
        print("Docker non-root execution, health, PostgreSQL persistence, Redis and graceful shutdown passed.", flush=True)
    except Exception:
        subprocess.run(command + ["logs", "--tail", "100"], cwd=ROOT, env=environment, check=False)
        subprocess.run(["docker", "logs", "--tail", "100", standalone], check=False)
        raise
    finally:
        subprocess.run(["docker", "rm", "--force", "--volumes", standalone], capture_output=True, check=False)
        subprocess.run(command + ["down", "--volumes", "--remove-orphans"], cwd=ROOT, env=environment, check=False)


if __name__ == "__main__":
    main()
