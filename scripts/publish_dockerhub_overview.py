#!/usr/bin/env python3
"""Publish the tracked Docker Hub overview without writing registry credentials."""

import json
import os
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen


def request(method, path, payload=None, session=None):
    headers = {"Content-Type": "application/json", "User-Agent": "LLMProxy-publisher"}
    if session:
        headers["Authorization"] = f"JWT {session}"
    data = json.dumps(payload).encode() if payload is not None else None
    http_request = Request(
        f"https://hub.docker.com/v2/{path}", data=data, headers=headers, method=method
    )
    try:
        with urlopen(http_request, timeout=30) as response:
            return json.load(response)
    except HTTPError as error:
        raise SystemExit(f"Docker Hub {method} failed with HTTP {error.code}.") from None
    except (URLError, TimeoutError):
        raise SystemExit(f"Docker Hub {method} could not reach the registry API.") from None


def main():
    username = os.environ.get("DOCKERHUB_USERNAME")
    token = os.environ.get("DOCKERHUB_TOKEN")
    if not username or not token:
        raise SystemExit("DOCKERHUB_USERNAME and DOCKERHUB_TOKEN must be configured.")
    overview = (Path(__file__).resolve().parents[1] / "docs/dockerhub.md").read_text()
    if not overview.strip() or len(overview) > 25000:
        raise SystemExit("Docker Hub overview must contain between 1 and 25000 characters.")

    login = request("POST", "users/login/", {"username": username, "password": token})
    session = login.get("token")
    if not session:
        raise SystemExit("Docker Hub login did not return a session token.")
    path = f"repositories/{quote(username, safe='')}/llmproxy/"
    request(
        "PATCH",
        path,
        {
            "description": "OpenAI-compatible LLM gateway: 10 providers, multiple keys, routing, quotas, batches and admin UI.",
            "full_description": overview,
        },
        session,
    )
    published = request("GET", path, session=session)
    if published.get("full_description") != overview:
        raise SystemExit("Docker Hub did not save the expected overview.")
    print(f"Verified Docker Hub overview: https://hub.docker.com/r/{username}/llmproxy")


if __name__ == "__main__":
    main()
