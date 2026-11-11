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

    def test_adoption_rule(self):
        base = {"exact_match": 0.90, "valid_json": 1.0}
        self.assertTrue(evaluate.adopt({"exact_match": 0.90, "valid_json": 0.99}, base))
        self.assertFalse(evaluate.adopt({"exact_match": 0.89, "valid_json": 1.0}, base))
        self.assertFalse(evaluate.adopt({"exact_match": 0.95, "valid_json": 0.98}, base))


if __name__ == "__main__":
    unittest.main()
