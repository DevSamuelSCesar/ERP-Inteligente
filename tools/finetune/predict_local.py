"""Predictions of the fine-tuned model served on this machine by an OpenAI-compatible server (llama.cpp, vLLM, Ollama).

Sends exactly what the app sends to the Local provider (system prompt + recipe catalog, then the request) to
http://127.0.0.1:PORT/v1/chat/completions and writes {"id", "prediction"} per test example. Loopback only, no key.
Standard library only.

    python tools/finetune/predict_local.py --data tools/finetune/data --out tools/finetune/data/pred_local.jsonl --port 8080
"""

import argparse
import json
import pathlib
import urllib.request

MODEL = "nexus-matcher"


def complete(port, messages, timeout=60):
    body = json.dumps({"model": MODEL, "messages": messages, "max_tokens": 300, "stream": False}).encode("utf-8")
    request = urllib.request.Request(f"http://127.0.0.1:{port}/v1/chat/completions", data=body,
                                     headers={"Content-Type": "application/json"}, method="POST")
    with urllib.request.urlopen(request, timeout=timeout) as response:
        data = json.loads(response.read().decode("utf-8"))
    return data["choices"][0]["message"].get("content") or ""


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--data", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--port", type=int, default=8080)
    args = parser.parse_args()
    if not 1024 <= args.port <= 65535:
        parser.error("the port must be between 1024 and 65535")

    test = [json.loads(line) for line in (pathlib.Path(args.data) / "test.jsonl").read_text(encoding="utf-8").splitlines() if line]
    with pathlib.Path(args.out).open("w", encoding="utf-8", newline="\n") as out:
        for row in test:
            text = complete(args.port, row["messages"][:2])
            out.write(json.dumps({"id": row["id"], "prediction": text}, ensure_ascii=False) + "\n")
    print(f"{len(test)} predictions -> {args.out}")


if __name__ == "__main__":
    main()
