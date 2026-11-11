"""Baseline of the experiment: Claude Haiku 5.5 with few-shot examples on the test set (references/IA.md, section 4).

Same system prompt and recipe catalog as the app, plus K examples from the training set as earlier turns. Writes one
JSONL line per test example: {"id", "prediction"} with the raw model text, for evaluate.py.

Needs the official SDK (pip install anthropic) and a key in the ANTHROPIC_API_KEY environment variable (or an active
`ant auth login` profile). The key is never written anywhere. Every run calls the API: ~100 short requests.

    python tools/finetune/predict_claude.py --data tools/finetune/data --out tools/finetune/data/pred_haiku.jsonl
"""

import argparse
import json
import pathlib
import random

import anthropic

MODEL = "claude-haiku-5-5"


def few_shot(train, k, seed):
    rng = random.Random(seed)
    in_scope = [r for r in train if r["output"].get("intent") != "none"]
    none = [r for r in train if r["output"].get("intent") == "none"]
    picked = rng.sample(in_scope, k - max(1, k // 4)) + rng.sample(none, max(1, k // 4))
    rng.shuffle(picked)
    turns = []
    for row in picked:
        turns.append({"role": "user", "content": row["input"]})
        turns.append({"role": "assistant", "content": json.dumps(row["output"], ensure_ascii=False)})
    return turns


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--data", required=True, help="Directory with train.jsonl and test.jsonl.")
    parser.add_argument("--out", required=True)
    parser.add_argument("--shots", type=int, default=8)
    parser.add_argument("--seed", type=int, default=7)
    args = parser.parse_args()

    data = pathlib.Path(args.data)
    train = [json.loads(line) for line in (data / "train.jsonl").read_text(encoding="utf-8").splitlines() if line]
    test = [json.loads(line) for line in (data / "test.jsonl").read_text(encoding="utf-8").splitlines() if line]
    examples = few_shot(train, args.shots, args.seed)

    client = anthropic.Anthropic()
    with pathlib.Path(args.out).open("w", encoding="utf-8", newline="\n") as out:
        for row in test:
            system = row["messages"][0]["content"]
            response = client.messages.create(
                model=MODEL,
                max_tokens=1024,
                system=system,
                messages=examples + [{"role": "user", "content": row["input"]}],
            )
            text = "".join(block.text for block in response.content if block.type == "text")
            out.write(json.dumps({"id": row["id"], "prediction": text}, ensure_ascii=False) + "\n")
    print(f"{len(test)} predictions -> {args.out}")


if __name__ == "__main__":
    main()
