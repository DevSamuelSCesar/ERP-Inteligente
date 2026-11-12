"""Metrics of the Recipe Matcher experiment (references/ARQUITETURA.md, section 10.1; references/IA.md, section 4).

Reads the gold test set and one or two prediction files (JSONL with "id" and "prediction", the raw model text) and
reports: valid JSON rate, exact match, micro F1 over parameter fields and accuracy on "no recipe". With --baseline it
applies the adoption rule: the candidate replaces the baseline only if its exact match is at least the baseline's and
it has at least 99% valid JSON. With --guard the predictions first go through the app's checks after the Matcher
(C# RecipeTemplate.Instantiate and RecipeGrounding): a recipe with missing or unknown parameters, or with a value that
is not in the request, counts as "no recipe", which is what the app does with it. Standard library only.

    python tools/finetune/evaluate.py --gold data/test.jsonl --pred local.jsonl --baseline haiku.jsonl --guard
"""

import argparse
import json
import pathlib
import re
import sys
import unicodedata

MIN_VALID_JSON = 0.99
RECIPES_FILE = pathlib.Path(__file__).resolve().parent / "recipes.json"

NUMBER_WORDS = {
    "zero": 0, "um": 1, "uma": 1, "dois": 2, "duas": 2, "tres": 3, "quatro": 4, "cinco": 5, "seis": 6, "sete": 7,
    "oito": 8, "nove": 9, "dez": 10, "onze": 11, "doze": 12, "treze": 13, "quatorze": 14, "catorze": 14, "quinze": 15,
    "dezesseis": 16, "dezessete": 17, "dezoito": 18, "dezenove": 19, "vinte": 20, "trinta": 30, "quarenta": 40,
    "cinquenta": 50, "sessenta": 60, "setenta": 70, "oitenta": 80, "noventa": 90, "cem": 100, "cento": 100,
    "duzentos": 200, "duzentas": 200, "trezentos": 300, "trezentas": 300, "quatrocentos": 400, "quatrocentas": 400,
    "quinhentos": 500, "quinhentas": 500, "seiscentos": 600, "seiscentas": 600, "setecentos": 700, "setecentas": 700,
    "oitocentos": 800, "oitocentas": 800, "novecentos": 900, "novecentas": 900,
}


# ---- the app's guard after the Matcher (reference of C# Core/Recipes/RecipeGrounding.cs: change both together) ----

def tokens(text):
    """Folded (case, accents), split on anything that is not a letter or digit, numbers without leading zeros."""
    folded = "".join(c for c in unicodedata.normalize("NFD", text or "") if unicodedata.category(c) != "Mn").lower()
    out = []
    for t in re.split(r"[^a-z0-9]+", unicodedata.normalize("NFC", folded)):
        if not t:
            continue
        out.append((t.lstrip("0") or "0") if t.isascii() and t.isdigit() else t)
    return out


def _leaves(node):
    if isinstance(node, dict):
        for value in node.values():
            yield from _leaves(value)
    elif isinstance(node, list):
        for value in node:
            yield from _leaves(value)
    else:
        yield node


def _claim(toks, used, wanted):
    for start in range(len(toks) - len(wanted) + 1):
        if all(not used[start + k] and toks[start + k] == wanted[k] for k in range(len(wanted))):
            for k in range(len(wanted)):
                used[start + k] = True
            return True
    return False


def _number_runs(toks):
    runs, i = [], 0
    while i < len(toks):
        if toks[i] not in NUMBER_WORDS:
            i += 1
            continue
        start, total = i, NUMBER_WORDS[toks[i]]
        i += 1
        while i + 1 < len(toks) and toks[i] == "e" and toks[i + 1] in NUMBER_WORDS:
            total += NUMBER_WORDS[toks[i + 1]]
            i += 2
        runs.append((start, i - start, total))
    return runs


def grounded(request, values):
    """(True, None) when every value is backed by its own tokens of the request, else (False, first parameter that is not)."""
    toks = tokens(request)
    used = [False] * len(toks)
    runs = _number_runs(toks)
    texts, numbers = [], []
    for name, node in values.items():
        for leaf in _leaves(node):
            if isinstance(leaf, bool) or leaf is None:
                continue
            if isinstance(leaf, str):
                texts.append((name, tokens(leaf)))
            elif isinstance(leaf, (int, float)):
                numbers.append((name, abs(leaf)))
    for name, wanted in sorted(texts, key=lambda t: -len(t[1])):
        if not wanted or not _claim(toks, used, wanted):
            return False, name
    for name, value in numbers:
        digits = [str(int(value))] if float(value).is_integer() else tokens(repr(float(value)))
        if _claim(toks, used, digits):
            continue
        run = next((r for r in runs if r[2] == value and not any(used[r[0]:r[0] + r[1]])), None)
        if run:
            for k in range(run[0], run[0] + run[1]):
                used[k] = True
            continue
        return False, name
    return True, None


def load_recipe_parameters():
    return {r["nome"]: set(r["parametros"]) for r in json.loads(RECIPES_FILE.read_text(encoding="utf-8"))}


def guard(request, answer, recipe_parameters):
    """What the app does with a parsed answer: missing/unknown parameters or an invented value -> no recipe."""
    if answer is None or answer["recipe"] is None:
        return answer
    expected = recipe_parameters.get(answer["recipe"])
    if expected is None or set(answer["params"]) != expected or not grounded(request, answer["params"])[0]:
        return {"recipe": None, "params": {}}
    return answer


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


def metrics(gold_rows, predictions, with_guard=False):
    """gold_rows: list of dataset rows; predictions: dict id -> raw text. Missing predictions count as invalid.

    with_guard applies the app's checks (guard()) to each parsed answer, using the row's "input" as the request.
    """
    recipe_parameters = load_recipe_parameters() if with_guard else None
    n = len(gold_rows)
    valid = exact = none_total = none_right = 0
    tp = fp = fn = 0
    for row in gold_rows:
        gold = canonical_gold(row["output"])
        pred = parse_prediction(predictions.get(row["id"]))
        if with_guard:
            pred = guard(row["input"], pred, recipe_parameters)
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
    parser.add_argument("--guard", action="store_true",
                        help="Also score after the app's checks; the adoption rule then uses those numbers.")
    args = parser.parse_args(argv)
    gold = read_jsonl(args.gold)
    report = {"candidate": metrics(gold, read_predictions(args.pred))}
    if args.guard:
        report["candidate_with_guard"] = metrics(gold, read_predictions(args.pred), with_guard=True)
    if args.baseline:
        report["baseline"] = metrics(gold, read_predictions(args.baseline))
        if args.guard:
            report["baseline_with_guard"] = metrics(gold, read_predictions(args.baseline), with_guard=True)
        key = "_with_guard" if args.guard else ""
        report["adopt_candidate"] = adopt(report["candidate" + key], report["baseline" + key])
    json.dump(report, sys.stdout, indent=2)
    print()
    return report


if __name__ == "__main__":
    main()
