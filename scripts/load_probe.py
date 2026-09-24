"""Measure gateway request/stream latency. The recovery drill uses this with a local mock."""
import argparse
import collections
import concurrent.futures
import json
import os
import statistics
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path


def percentile(values, fraction):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int((len(ordered) - 1) * fraction))]


def probe(urls, key, duration=60, concurrency=8, timeout=60):
    started = time.monotonic()
    deadline = started + duration
    lock = threading.Lock()
    results = []

    def worker(index):
        iteration = 0
        while time.monotonic() < deadline:
            streaming = (index + iteration) % 2 == 0
            base = urls[(index + iteration) % len(urls)].rstrip("/")
            body = json.dumps({"model": "fast", "messages": [{"role": "user", "content": "Hello"}],
                               "stream": streaming, "max_tokens": 16}).encode()
            request = urllib.request.Request(base + "/v1/chat/completions", data=body, headers={
                "Authorization": "Bearer " + key, "Content-Type": "application/json"})
            begin, first, error = time.monotonic(), None, None
            try:
                with urllib.request.urlopen(request, timeout=timeout) as response:
                    if streaming:
                        done = False
                        for raw in response:
                            if not raw.startswith(b"data: "):
                                continue
                            data = raw[6:].strip()
                            if data == b"[DONE]":
                                done = True
                                break
                            event = json.loads(data)
                            if "error" in event:
                                error = event["error"].get("code", "stream_error")
                            if first is None and any(choice.get("delta", {}).get("content")
                                                     for choice in event.get("choices", [])):
                                first = (time.monotonic() - begin) * 1000
                        if not done:
                            error = error or "incomplete_stream"
                    else:
                        payload = json.load(response)
                        if not payload.get("choices"):
                            error = "invalid_response"
            except urllib.error.HTTPError as exception:
                error = "http_" + str(exception.code)
            except (OSError, ValueError, TimeoutError):
                error = "connection_or_protocol_error"
            with lock:
                results.append(((time.monotonic() - begin) * 1000, first, error))
            iteration += 1

    with concurrent.futures.ThreadPoolExecutor(max_workers=concurrency) as executor:
        list(executor.map(worker, range(concurrency)))
    elapsed = time.monotonic() - started
    latencies = [latency for latency, _, error in results if error is None]
    first_tokens = [first for _, first, error in results if first is not None and error is None]
    failures = collections.Counter(error for _, _, error in results if error is not None)
    return {"duration_seconds": round(elapsed, 3), "requests": len(results), "completed": len(latencies),
            "error_rate": sum(failures.values()) / max(1, len(results)), "errors": dict(failures),
            "requests_per_second": round(len(results) / elapsed, 3),
            "latency_ms": {"p50": percentile(latencies, .5), "p95": percentile(latencies, .95),
                           "p99": percentile(latencies, .99), "mean": statistics.mean(latencies) if latencies else None},
            "first_token_ms": {"p50": percentile(first_tokens, .5), "p95": percentile(first_tokens, .95)}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", action="append", required=True)
    parser.add_argument("--duration", type=float, default=60)
    parser.add_argument("--concurrency", type=int, default=8)
    parser.add_argument("--max-error-rate", type=float, default=.01)
    parser.add_argument("--max-p95-ms", type=float, default=2000)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    if args.duration <= 0 or args.concurrency < 1:
        parser.error("duration and concurrency must be positive")
    result = probe(args.url, os.environ["LLMPROXY_API_KEY"], args.duration, args.concurrency)
    encoded = json.dumps(result, indent=2)
    print(encoded)
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(encoded + "\n")
    if result["completed"] == 0 or result["error_rate"] > args.max_error_rate or result["latency_ms"]["p95"] > args.max_p95_ms:
        raise SystemExit(1)


if __name__ == "__main__":
    main()
