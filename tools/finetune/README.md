# Recipe Matcher fine-tuning experiment

Feature 8 of `ROADMAP.md`; design in `references/ARQUITETURA.md` (section 10.1) and `references/IA.md` (section 4).
Task: a warehouse request in Portuguese becomes `{"intent", "recipe", "params"}`, or `{"intent": "none"}` when no
recipe fits. The fine-tuned model replaces Claude Haiku 5.5 as the Matcher only if it ties or beats it on exact match
and has at least 99% valid JSON.

| Step | Command | Needs |
|------|---------|-------|
| 1. Dataset (500 examples, 400 train / 100 test, 15% out of scope) | `python tools/finetune/generate_dataset.py` | Standard library |
| 2. Baseline: Haiku 5.5 few-shot | `python tools/finetune/predict_claude.py --data tools/finetune/data --out tools/finetune/data/pred_haiku.jsonl` | `anthropic` SDK, `ANTHROPIC_API_KEY` |
| 3. LoRA training (Qwen2.5-1.5B-Instruct) | `python tools/finetune/train_lora.py --data tools/finetune/data --out tools/finetune/out/nexus-matcher` | GPU, `requirements-train.txt` |
| 4. Serve the model on `127.0.0.1` as `nexus-matcher` | e.g. merge the adapter, convert to GGUF and run `llama-server --port 8080 --alias nexus-matcher` | llama.cpp, vLLM or Ollama |
| 5. Candidate predictions | `python tools/finetune/predict_local.py --data tools/finetune/data --out tools/finetune/data/pred_local.jsonl` | Standard library |
| 6. Metrics and adoption | `python tools/finetune/evaluate.py --gold tools/finetune/data/test.jsonl --pred tools/finetune/data/pred_local.jsonl --baseline tools/finetune/data/pred_haiku.jsonl` | Standard library |

If `adopt_candidate` is true, an administrator picks **Modelo local** for "Receitas (Matcher)" in Configurações (port of
the local server there). The app accepts the dataset's answer format, and the system prompt of every example is
`Core/Recipes/Prompts/matcher.md`, the same file the app embeds: train and production inputs match.

Data is fictitious (catalog of `db/seed.sql` plus the recipes in `recipes.json`). Nothing here stores a key.
