"""Serves the fine-tuned Matcher and a local embeddings model on 127.0.0.1 with the OpenAI wire format.

What the app's Local provider (Configurações > Modelo local) and the local embeddings option call:

    POST /v1/chat/completions   model "nexus-matcher": the merged model written by train_lora.py (greedy decoding)
    POST /v1/embeddings         model "nexus-embed": a multilingual sentence model, mean pooling (384 dimensions)
    GET  /v1/models

Loopback only (the address is fixed), no key, one request at a time per model. Either model may be left out.

    python tools/finetune/serve_local.py --matcher <dir>/nexus-matcher/merged --port 8080
    python tools/finetune/serve_local.py --no-matcher --port 8080          # embeddings only

Needs the packages in requirements-train.txt; the request/response helpers use the standard library and are tested in
tools/tests/test_finetune.py.
"""

import argparse
import json
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

MATCHER_ALIAS = "nexus-matcher"
EMBED_ALIAS = "nexus-embed"
EMBED_MODEL = "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2"
HOST = "127.0.0.1"
MAX_BODY = 1_000_000
MAX_NEW_TOKENS = 512
MAX_INPUTS = 64
MAX_INPUT_CHARS = 8000


class BadRequest(Exception):
    pass


def parse_chat(body):
    """The messages and the token limit of a chat completions request (only what the app sends)."""
    if body.get("model") != MATCHER_ALIAS:
        raise BadRequest(f"model must be {MATCHER_ALIAS}")
    messages = body.get("messages")
    if not isinstance(messages, list) or not messages:
        raise BadRequest("messages must be a non-empty list")
    clean = []
    for m in messages:
        if not isinstance(m, dict) or m.get("role") not in ("system", "user", "assistant") or not isinstance(m.get("content"), str):
            raise BadRequest("each message needs a role (system, user, assistant) and text content")
        clean.append({"role": m["role"], "content": m["content"]})
    limit = body.get("max_tokens", body.get("max_completion_tokens", 300))
    if not isinstance(limit, int) or limit < 1:
        raise BadRequest("max_tokens must be a positive integer")
    return clean, min(limit, MAX_NEW_TOKENS)


def parse_embeddings(body):
    """The texts of an embeddings request: a string or a list of strings."""
    if body.get("model") != EMBED_ALIAS:
        raise BadRequest(f"model must be {EMBED_ALIAS}")
    texts = body.get("input")
    if isinstance(texts, str):
        texts = [texts]
    if not isinstance(texts, list) or not texts or len(texts) > MAX_INPUTS or not all(isinstance(t, str) for t in texts):
        raise BadRequest(f"input must be a string or a list of 1 to {MAX_INPUTS} strings")
    return [t[:MAX_INPUT_CHARS] for t in texts]


def chat_response(text, prompt_tokens, completion_tokens):
    return {
        "id": f"chatcmpl-local-{int(time.time() * 1000)}",
        "object": "chat.completion",
        "created": int(time.time()),
        "model": MATCHER_ALIAS,
        "choices": [{"index": 0, "message": {"role": "assistant", "content": text}, "finish_reason": "stop"}],
        "usage": {"prompt_tokens": prompt_tokens, "completion_tokens": completion_tokens,
                  "total_tokens": prompt_tokens + completion_tokens},
    }


def embeddings_response(vectors, tokens):
    return {
        "object": "list",
        "model": EMBED_ALIAS,
        "data": [{"object": "embedding", "index": i, "embedding": [float(x) for x in v]} for i, v in enumerate(vectors)],
        "usage": {"prompt_tokens": tokens, "total_tokens": tokens},
    }


class Matcher:
    def __init__(self, path):
        import torch
        from transformers import AutoModelForCausalLM, AutoTokenizer
        self.torch = torch
        self.tokenizer = AutoTokenizer.from_pretrained(path)
        device = "cuda" if torch.cuda.is_available() else "cpu"
        self.model = AutoModelForCausalLM.from_pretrained(
            path, dtype=torch.float16 if device == "cuda" else torch.float32, device_map={"": device}).eval()
        self.lock = threading.Lock()

    def complete(self, messages, max_new_tokens):
        prompt = self.tokenizer.apply_chat_template(messages, tokenize=False, add_generation_prompt=True)
        inputs = self.tokenizer(prompt, return_tensors="pt", add_special_tokens=False).to(self.model.device)
        with self.lock, self.torch.inference_mode():
            output = self.model.generate(**inputs, max_new_tokens=max_new_tokens, do_sample=False,
                                         pad_token_id=self.tokenizer.eos_token_id)
        new = output[0][inputs["input_ids"].shape[1]:]
        return self.tokenizer.decode(new, skip_special_tokens=True).strip(), inputs["input_ids"].shape[1], len(new)


class Embedder:
    def __init__(self, name):
        import torch
        from transformers import AutoModel, AutoTokenizer
        self.torch = torch
        self.tokenizer = AutoTokenizer.from_pretrained(name)
        device = "cuda" if torch.cuda.is_available() else "cpu"
        self.model = AutoModel.from_pretrained(name).to(device).eval()
        self.lock = threading.Lock()

    def embed(self, texts):
        batch = self.tokenizer(texts, padding=True, truncation=True, max_length=512, return_tensors="pt").to(self.model.device)
        with self.lock, self.torch.inference_mode():
            hidden = self.model(**batch).last_hidden_state
        mask = batch["attention_mask"].unsqueeze(-1).to(hidden.dtype)
        vectors = (hidden * mask).sum(1) / mask.sum(1).clamp(min=1e-9)
        return vectors.float().cpu().tolist(), int(batch["attention_mask"].sum())


def make_handler(matcher, embedder):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, fmt, *args):  # no request bodies in the log
            print(f"{self.command} {self.path} -> {args[1] if len(args) > 1 else ''}")

        def send_json(self, status, payload):
            data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)

        def error(self, status, message):
            self.send_json(status, {"error": {"message": message, "type": "invalid_request_error"}})

        def do_GET(self):
            if self.path != "/v1/models":
                return self.error(404, "not found")
            models = ([MATCHER_ALIAS] if matcher else []) + ([EMBED_ALIAS] if embedder else [])
            self.send_json(200, {"object": "list", "data": [{"id": m, "object": "model", "owned_by": "local"} for m in models]})

        def do_POST(self):
            length = int(self.headers.get("Content-Length") or 0)
            if length <= 0 or length > MAX_BODY:
                return self.error(413, "body too large or empty")
            try:
                body = json.loads(self.rfile.read(length).decode("utf-8"))
                if not isinstance(body, dict):
                    raise BadRequest("the body must be a JSON object")
                if self.path == "/v1/chat/completions" and matcher:
                    messages, limit = parse_chat(body)
                    text, prompt_tokens, completion_tokens = matcher.complete(messages, limit)
                    return self.send_json(200, chat_response(text, prompt_tokens, completion_tokens))
                if self.path == "/v1/embeddings" and embedder:
                    vectors, tokens = embedder.embed(parse_embeddings(body))
                    return self.send_json(200, embeddings_response(vectors, tokens))
                return self.error(404, "not found")
            except (BadRequest, ValueError) as ex:
                return self.error(400, str(ex))

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--matcher", help="directory of the merged model (train_lora.py --out .../merged)")
    parser.add_argument("--no-matcher", action="store_true")
    parser.add_argument("--embed-model", default=EMBED_MODEL)
    parser.add_argument("--no-embeddings", action="store_true")
    parser.add_argument("--port", type=int, default=8080)
    args = parser.parse_args()
    if not 1024 <= args.port <= 65535:
        parser.error("the port must be between 1024 and 65535")
    if not args.no_matcher and not args.matcher:
        parser.error("--matcher is required unless --no-matcher")

    matcher = None if args.no_matcher else Matcher(args.matcher)
    embedder = None if args.no_embeddings else Embedder(args.embed_model)
    server = ThreadingHTTPServer((HOST, args.port), make_handler(matcher, embedder))
    print(f"Serving on http://{HOST}:{args.port} ({', '.join(m for m, on in ((MATCHER_ALIAS, matcher), (EMBED_ALIAS, embedder)) if on)})",
          flush=True)
    server.serve_forever()


if __name__ == "__main__":
    main()
