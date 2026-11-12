"""Tests for tools/finetune (dataset generator and evaluator of the Recipe Matcher experiment).

Run from the repository root:  python3 -m unittest discover -s tools/tests -v
Standard library only: training and the API baseline are not exercised here.
"""

import json
import pathlib
import sys
import unittest

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "finetune"))

import evaluate  # noqa: E402
import generate_dataset  # noqa: E402
import serve_local  # noqa: E402
import train_lora  # noqa: E402


class DatasetTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.train, cls.test = generate_dataset.build(seed=42)
        cls.recipes = {r["nome"]: r for r in generate_dataset.load_recipes()}

    def test_split_is_400_train_and_100_test(self):
        self.assertEqual((len(self.train), len(self.test)), (400, 100))
        self.assertEqual(len({r["id"] for r in self.train + self.test}), 500)

    def test_about_15_percent_is_out_of_scope(self):
        none = sum(1 for r in self.train + self.test if r["output"] == {"intent": "none"})
        self.assertEqual(none, 75)
        self.assertGreater(sum(1 for r in self.test if r["output"] == {"intent": "none"}), 5)

    def test_is_deterministic_for_a_seed(self):
        again_train, again_test = generate_dataset.build(seed=42)
        self.assertEqual([r["input"] for r in again_test], [r["input"] for r in self.test])
        other_train, _ = generate_dataset.build(seed=7)
        self.assertNotEqual([r["input"] for r in other_train], [r["input"] for r in self.train])

    def test_every_answer_fills_exactly_the_parameters_of_its_recipe(self):
        for row in self.train + self.test:
            output = row["output"]
            if output == {"intent": "none"}:
                continue
            with self.subTest(row=row["id"]):
                recipe = self.recipes[output["recipe"]]
                self.assertEqual(set(output["params"]), set(recipe["parametros"]))
                self.assertEqual(output["intent"], recipe["intent"])
                self.assertRegex(output["params"]["sku"], r"^[A-Z]{3}-[0-9]{3}$")  # normalized even when typed in lower case
                self.assertIsInstance(output["params"]["qty"], int)

    def test_messages_use_the_app_prompt_and_catalog(self):
        row = self.test[0]
        system, user, assistant = row["messages"]
        prompt = (generate_dataset.ROOT / "Core" / "Recipes" / "Prompts" / "matcher.md").read_text(encoding="utf-8")
        self.assertTrue(system["content"].startswith(prompt.replace("\r\n", "\n").rstrip()))
        self.assertIn('<dados fonte="receitas">', system["content"])
        # The prompt itself names the block; the catalog is the last one and never contains a raw '<'.
        self.assertNotIn("<", system["content"].rsplit('<dados fonte="receitas">', 1)[1].rsplit("</dados>", 1)[0])
        self.assertEqual(user["content"], row["input"])
        self.assertEqual(json.loads(assistant["content"]), row["output"])

    def test_values_survive_the_noise(self):
        for row in self.train:
            output = row["output"]
            if output == {"intent": "none"}:
                continue
            with self.subTest(row=row["id"]):
                self.assertIn(output["params"]["sku"].lower(), row["input"].lower())
                if "reason" in output["params"]:  # free text is repeated verbatim: never altered by the noise
                    self.assertIn(output["params"]["reason"], row["input"])
                self.assertNotIn(generate_dataset.PROTECTED, row["input"])


class HoldoutTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.rows = generate_dataset.holdout()
        cls.recipes = {r["nome"]: r for r in generate_dataset.load_recipes()}

    def test_has_40_cases_with_unique_ids_and_a_quarter_out_of_scope(self):
        self.assertEqual(len(self.rows), 40)
        self.assertEqual(len({r["id"] for r in self.rows}), 40)
        self.assertEqual(sum(1 for r in self.rows if r["output"] == {"intent": "none"}), 10)

    def test_answers_fill_exactly_the_parameters_of_their_recipe_with_values_from_the_request(self):
        for row in self.rows:
            output = row["output"]
            if output == {"intent": "none"}:
                continue
            with self.subTest(row=row["id"]):
                recipe = self.recipes[output["recipe"]]
                self.assertEqual(set(output["params"]), set(recipe["parametros"]))
                self.assertEqual(output["intent"], recipe["intent"])
                self.assertIn(output["params"]["sku"].lower(), row["input"].lower())
                if "reason" in output["params"]:
                    self.assertIn(output["params"]["reason"], row["input"])
                    self.assertLess(output["params"]["qty"], 0)

    def test_no_case_is_in_the_generated_sets(self):
        train, test = generate_dataset.build(seed=42)
        generated = {r["input"].lower() for r in train + test}
        self.assertFalse([r["id"] for r in self.rows if r["input"].lower() in generated])

    def test_messages_match_the_generated_ones(self):
        system = generate_dataset.build(seed=42)[1][0]["messages"][0]
        self.assertEqual(self.rows[0]["messages"][0], system)
        self.assertEqual(self.rows[0]["messages"][1]["content"], self.rows[0]["input"])


class EvaluateTests(unittest.TestCase):
    GOLD = [
        {"id": "a", "output": {"intent": "putaway", "recipe": "armazenar_da_doca",
                               "params": {"sku": "ABC-123", "qty": 40, "dock": "DOCA-01", "target": "A-01-01"}}},
        {"id": "b", "output": {"intent": "none"}},
        {"id": "c", "output": {"intent": "receive", "recipe": "receber_na_doca",
                               "params": {"sku": "FER-010", "qty": 12, "location": "DOCA-02"}}},
        {"id": "d", "output": {"intent": "none"}},
    ]

    def test_parse_accepts_both_answer_shapes(self):
        self.assertEqual(evaluate.parse_prediction('{"receita": null}'), {"recipe": None, "params": {}})
        self.assertEqual(evaluate.parse_prediction('```json\n{"intent":"none"}\n```'), {"recipe": None, "params": {}})
        self.assertEqual(evaluate.parse_prediction('{"receita":"x","parametros":{"a":1}}'), {"recipe": "x", "params": {"a": 1}})
        self.assertIsNone(evaluate.parse_prediction("não sei"))
        self.assertIsNone(evaluate.parse_prediction('{"recipe":"x"}'))
        self.assertIsNone(evaluate.parse_prediction(None))

    def test_metrics(self):
        predictions = {
            "a": '{"recipe":"armazenar_da_doca","params":{"sku":"ABC-123","qty":40,"dock":"DOCA-01","target":"A-01-01"}}',
            "b": '{"intent":"none"}',
            "c": '{"recipe":"receber_na_doca","params":{"sku":"FER-010","qty":21,"location":"DOCA-02"}}',  # one wrong field
            "d": "texto sem JSON",
        }
        m = evaluate.metrics(self.GOLD, predictions)
        self.assertEqual(m["n"], 4)
        self.assertEqual(m["valid_json"], 0.75)
        self.assertEqual(m["exact_match"], 0.5)
        self.assertEqual(m["none_accuracy"], 0.5)
        # 7 gold pairs; 6 right, 1 wrong predicted (fp) and 1 missed (fn): P = R = 6/7.
        self.assertAlmostEqual(m["field_f1"], 6 / 7)

    def test_missing_predictions_count_as_invalid(self):
        self.assertEqual(evaluate.metrics(self.GOLD, {})["valid_json"], 0.0)

    def test_grounding_shares_the_vectors_of_the_app(self):
        path = pathlib.Path(evaluate.__file__).resolve().parent / "grounding_cases.json"
        cases = json.loads(path.read_text(encoding="utf-8"))
        self.assertGreaterEqual(len(cases), 10)
        for case in cases:
            with self.subTest(request=case["request"]):
                ok, name = evaluate.grounded(case["request"], case["values"])
                self.assertEqual(ok, case["ungrounded"] is None)
                self.assertEqual(name, case["ungrounded"])
        self.assertEqual(evaluate.tokens("DOCA-01 no A-02-01, Recepção"), ["doca", "1", "no", "a", "2", "1", "recepcao"])

    def test_guard_turns_invented_or_incomplete_answers_into_no_recipe(self):
        gold = [
            {"id": "a", "input": "guarda 15 LIM-201 da doca 2", "output": {"intent": "none"}},
            {"id": "b", "input": "recebe 50 ABC-123", "output": {"intent": "none"}},
            {"id": "c", "input": "receba 12 FER-010 na doca 2", "output": self.GOLD[2]["output"]},
        ]
        predictions = {
            "a": '{"recipe":"armazenar_da_doca","params":{"sku":"LIM-201","qty":15,"dock":"DOCA-02","target":"B-01-01"}}',
            "b": '{"recipe":"receber_na_doca","params":{"sku":"ABC-123","qty":50}}',
            "c": '{"recipe":"receber_na_doca","params":{"sku":"FER-010","qty":12,"location":"DOCA-02"}}',
        }
        self.assertEqual(evaluate.metrics(gold, predictions)["exact_match"], 1 / 3)
        guarded = evaluate.metrics(gold, predictions, with_guard=True)
        self.assertEqual(guarded["exact_match"], 1.0)
        self.assertEqual(guarded["none_accuracy"], 1.0)

    def test_adoption_rule(self):
        base = {"exact_match": 0.90, "valid_json": 1.0}
        self.assertTrue(evaluate.adopt({"exact_match": 0.90, "valid_json": 0.99}, base))
        self.assertFalse(evaluate.adopt({"exact_match": 0.89, "valid_json": 1.0}, base))
        self.assertFalse(evaluate.adopt({"exact_match": 0.95, "valid_json": 0.98}, base))


if __name__ == "__main__":
    unittest.main()


class ServeLocalTests(unittest.TestCase):
    """Request validation and response shapes of the local server (the models themselves are not loaded here)."""

    def test_chat_request_keeps_only_text_messages_and_caps_the_tokens(self):
        messages, limit = serve_local.parse_chat({"model": "nexus-matcher", "max_tokens": 9999, "messages": [
            {"role": "system", "content": "s", "extra": 1}, {"role": "user", "content": "u"}]})
        self.assertEqual(messages, [{"role": "system", "content": "s"}, {"role": "user", "content": "u"}])
        self.assertEqual(limit, serve_local.MAX_NEW_TOKENS)

    def test_chat_request_is_validated(self):
        for body in ({"model": "gpt-5", "messages": [{"role": "user", "content": "x"}]},
                     {"model": "nexus-matcher", "messages": []},
                     {"model": "nexus-matcher", "messages": [{"role": "tool", "content": "x"}]},
                     {"model": "nexus-matcher", "messages": [{"role": "user", "content": "x"}], "max_tokens": 0}):
            with self.subTest(body=body), self.assertRaises(serve_local.BadRequest):
                serve_local.parse_chat(body)

    def test_embeddings_request_accepts_a_string_or_a_list(self):
        self.assertEqual(serve_local.parse_embeddings({"model": "nexus-embed", "input": "a"}), ["a"])
        self.assertEqual(serve_local.parse_embeddings({"model": "nexus-embed", "input": ["a", "b"]}), ["a", "b"])
        for body in ({"model": "x", "input": "a"}, {"model": "nexus-embed", "input": []},
                     {"model": "nexus-embed", "input": ["a"] * 65}, {"model": "nexus-embed", "input": [1]}):
            with self.subTest(body=body), self.assertRaises(serve_local.BadRequest):
                serve_local.parse_embeddings(body)

    def test_responses_have_the_openai_shape_the_app_reads(self):
        chat = serve_local.chat_response('{"intent":"none"}', 10, 5)
        self.assertEqual(chat["choices"][0]["message"], {"role": "assistant", "content": '{"intent":"none"}'})
        self.assertEqual(chat["usage"]["total_tokens"], 15)
        emb = serve_local.embeddings_response([[0.5, 1], [2, 3]], 7)
        self.assertEqual([d["index"] for d in emb["data"]], [0, 1])
        self.assertEqual(emb["data"][1]["embedding"], [2.0, 3.0])
        self.assertEqual(emb["usage"]["prompt_tokens"], 7)

    def test_server_binds_only_to_loopback(self):
        self.assertEqual(serve_local.HOST, "127.0.0.1")


class TrainLabelTests(unittest.TestCase):
    """The loss covers only the answer: prompt tokens are masked with -100."""

    class Tokenizer:
        def apply_chat_template(self, messages, tokenize=False, add_generation_prompt=False):
            text = "".join(f"<{m['role']}>{m['content']}" for m in messages)
            return text + ("<assistant>" if add_generation_prompt else "")

        def __call__(self, text, add_special_tokens=False):
            return {"input_ids": [ord(c) for c in text]}

    def test_only_answer_tokens_are_labelled(self):
        messages = [{"role": "system", "content": "S"}, {"role": "user", "content": "U"},
                    {"role": "assistant", "content": "{}"}]
        row = train_lora.encode(self.Tokenizer(), messages, 1024)
        prompt_len = len("<system>S<user>U<assistant>")
        self.assertEqual(row["labels"][:prompt_len], [train_lora.IGNORE] * prompt_len)
        self.assertEqual(row["labels"][prompt_len:], [ord("{"), ord("}")])
        self.assertEqual(len(row["input_ids"]), len(row["attention_mask"]))

    def test_truncation_keeps_labels_aligned(self):
        messages = [{"role": "system", "content": "S" * 50}, {"role": "user", "content": "U"},
                    {"role": "assistant", "content": "{}"}]
        row = train_lora.encode(self.Tokenizer(), messages, 10)
        self.assertEqual(len(row["labels"]), 10)
        self.assertTrue(all(t == train_lora.IGNORE for t in row["labels"]))

