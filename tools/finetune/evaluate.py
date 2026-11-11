"""Metrics of the Recipe Matcher experiment (references/ARQUITETURA.md, section 10.1; references/IA.md, section 4).

Reads the gold test set and one or two prediction files (JSONL with "id" and "prediction", the raw model text) and
reports: valid JSON rate, exact match, micro F1 over parameter fields and accuracy on "no recipe". With --baseline it
applies the adoption rule: the candidate replaces the baseline only if its exact match is at least the baseline's and
it has at least 99% valid JSON. Standard library only.

    python tools/finetune/evaluate.py --gold data/test.jsonl --pred local.jsonl --baseline haiku.jsonl
"""

import argparse
import json
import pathlib
import sys

MIN_VALID_JSON = 0.99


def parse_prediction(text):
    """Returns the canonical answer {"recipe": name or None, "params": {...}} or None when the text is not usable JSON.

    Accepts both shapes the app accepts (C# RecipeMatcher.Parse): {"receita", "parametros"} and
    {"intent", "recipe", "params"}; {"intent": "none"} and {"receita": null} mean no recipe.
    """
    if not isinstance(text, str):
        return None
    start, end = text.find("{"), text.rfind("}")
    if start < 0 or end <= start:
        return None
    try:
        data = json.loads(text[start:end + 1])
    except json.JSONDecodeError:
        return None
    if not isinstance(data, dict):
        return None
    if data.get("intent") == "none" or ("receita" in data and data["receita"] is None) or ("recipe" in data and data["recipe"] is None):
        return {"recipe": None, "params": {}}
    name = data.get("recipe", data.get("receita"))
    params = data.get("params", data.get("parametros"))
    if not isinstance(name, str) or not isinstance(params, dict):
        return None
    return {"recipe": name, "params": params}


def canonical_gold(output):
    if output.get("intent") == "none":
        return {"recipe": None, "params": {}}
    return {"recipe": output["recipe"], "params": output["params"]}


def _pairs(answer):
    return {(k, json.dumps(v, sort_keys=True, ensure_ascii=False)) for k, v in answer["params"].items()} if answer else set()


def metrics(gold_rows, predictions):
    """gold_rows: list of dataset rows; predictions: dict id -> raw text. Missing predictions count as invalid."""
    n = len(gold_rows)
    valid = exact = none_total = none_right = 0
    tp = fp = fn = 0
    for row in gold_rows:
        gold = canonical_gold(row["output"])
        pred = parse_prediction(predictions.get(row["id"]))
        if pred is not None:
            valid += 1
        if pred is not None and pred["recipe"] == gold["recipe"] and _pairs(pred) == _pairs(gold):
            exact += 1
        if gold["recipe"] is None:
            none_total += 1
            if pred is not None and pred["recipe"] is None:
                none_right += 1
        else:
            gold_pairs = _pairs(gold)
            pred_pairs = _pairs(pred) if pred is not None and pred["recipe"] == gold["recipe"] else set()
            tp += len(gold_pairs & pred_pairs)
            fp += len(pred_pairs - gold_pairs)
            fn += len(gold_pairs - pred_pairs)
    precision = tp / (tp + fp) if tp + fp else 0.0
    recall = tp / (tp + fn) if tp + fn else 0.0
    f1 = 2 * precision * recall / (precision + recall) if precision + recall else 0.0
    return {
        "n": n,
        "valid_json": valid / n if n else 0.0,
        "exact_match": exact / n if n else 0.0,
        "field_f1": f1,
        "none_accuracy": none_right / none_total if none_total else 0.0,
    }


def adopt(candidate, baseline):
    """The adoption rule of references/IA.md: tie or beat the baseline on exact match, with >= 99% valid JSON."""
    return candidate["exact_match"] >= baseline["exact_match"] and candidate["valid_json"] >= MIN_VALID_JSON


def read_jsonl(path):
    with pathlib.Path(path).open(encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def read_predictions(path):
    return {row["id"]: row.get("prediction") for row in read_jsonl(path)}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--gold", required=True)
    parser.add_argument("--pred", required=True, help="Predictions of the candidate (e.g. the fine-tuned model).")
    parser.add_argument("--baseline", help="Predictions of the baseline (e.g. Claude Haiku 5.5 few-shot).")
    args = parser.parse_args(argv)
    gold = read_jsonl(args.gold)
    report = {"candidate": metrics(gold, read_predictions(args.pred))}
    if args.baseline:
        report["baseline"] = metrics(gold, read_predictions(args.baseline))
        report["adopt_candidate"] = adopt(report["candidate"], report["baseline"])
    json.dump(report, sys.stdout, indent=2)
    print()
    return report


if __name__ == "__main__":
    main()
