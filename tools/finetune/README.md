# Recipe Matcher fine-tuning experiment

Feature 8 of `ROADMAP.md`; design in `references/ARQUITETURA.md` (section 10.1) and `references/IA.md` (section 4).
Task: a warehouse request in Portuguese becomes `{"intent", "recipe", "params"}`, or `{"intent": "none"}` when no
recipe fits. The fine-tuned model replaces Claude Haiku 5.5 as the Matcher only if it ties or beats it on exact match
and has at least 99% valid JSON.

| Step | Command | Needs |
|------|---------|-------|
| 1. Dataset (500 examples, 400 train / 100 test, 15% out of scope) and the hand-written holdout (40, from `holdout.json`) | `python tools/finetune/generate_dataset.py` | Standard library |
| 2. Baseline: Haiku 5.5 few-shot | `python tools/finetune/predict_claude.py --data tools/finetune/data --out tools/finetune/data/pred_haiku.jsonl` | `anthropic` SDK, `ANTHROPIC_API_KEY` |
| 3. LoRA training (Qwen2.5-1.5B-Instruct) | `python tools/finetune/train_lora.py --data tools/finetune/data --out DIR/nexus-matcher` | CUDA GPU, `requirements-train.txt` |
| 4. Serve on `127.0.0.1` (Matcher `nexus-matcher` and embeddings `nexus-embed`) | `python tools/finetune/serve_local.py --matcher DIR/nexus-matcher/merged --port 8080` | `requirements-train.txt` |
| 5. Candidate predictions | `python tools/finetune/predict_local.py --data tools/finetune/data --out tools/finetune/data/pred_local.jsonl --port 8080` | Standard library |
| 6. Metrics and adoption | `python tools/finetune/evaluate.py --gold tools/finetune/data/test.jsonl --pred tools/finetune/data/pred_local.jsonl --baseline tools/finetune/data/pred_haiku.jsonl --guard` | Standard library |

Steps 2 and 5 take `--split holdout` to predict the hand-written set; score it with `--gold tools/finetune/data/holdout.jsonl`.
The generated test set shares the generator's templates with the training set, so it measures the templates; the
holdout measures generalization. `--guard` also scores the answers after the app's checks (missing parameters, and
`RecipeGrounding`: every value must come from the request), and the adoption rule then uses those numbers.

Notes on step 3:

- The loss is computed only on the answer (the prompt tokens are masked), with the plain `transformers` Trainer and PEFT.
- It was run on a 6 GB GTX 1660 with `--no-4bit` (fp16 weights, batch 1 × 16 accumulation, gradient checkpointing). The
  4-bit QLoRA default fits smaller GPUs but was slower there, because the activations spilled into shared memory.
- bf16 is used only where the GPU has it natively (compute capability 8.0+); Turing cards train in fp16.
- Keep `DIR` outside the repository (the merged model is about 3 GB): nothing under `out/` is committed.

Step 4 also serves the embeddings of the hybrid RAG search (`paraphrase-multilingual-MiniLM-L12-v2`, 384 dimensions);
`--no-matcher` serves only them. In Configurações, an administrator picks **Modelo local** for "Receitas (Matcher)" (if
`adopt_candidate` is true) and/or for "Busca por significado", with the port of the server. The app accepts the dataset's
answer format, and the system prompt of every example is `Core/Recipes/Prompts/matcher.md`, the same file the app embeds:
train and production inputs match.

Results of the first run (2026-11-12, GTX 1660, 3 h of training; details in `references/IA.md`, section 4):

| Set | Valid JSON | Exact match | Field F1 | "No recipe" |
|-----|-----------:|------------:|---------:|------------:|
| Generated test (100) | 100% | 100% | 1.000 | 100% |
| Holdout (40), raw | 100% | 80.0% | 0.991 | 30% |
| Holdout (40), `--guard` | 100% | 92.5% | 0.986 | 80% |

The model fills a missing value with a plausible one; the app's grounding check turns that into "no recipe". About 6 s
per request on that GPU (fp16 transformers). The Haiku baseline is pending an Anthropic key.

Data is fictitious (catalog of `db/seed.sql` plus the recipes in `recipes.json`). Nothing here stores a key.
