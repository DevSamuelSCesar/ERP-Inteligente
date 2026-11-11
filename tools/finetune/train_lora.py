"""LoRA fine-tuning of a small open model as the Recipe Matcher (references/ARQUITETURA.md, section 10.1).

Trains on the "messages" of train.jsonl (system prompt + recipe catalog, request, expected JSON), computing the loss
only on the assistant answer. Needs a GPU and the packages in requirements-train.txt; it is not run by the test suite.

    pip install -r tools/finetune/requirements-train.txt
    python tools/finetune/train_lora.py --data tools/finetune/data --out tools/finetune/out/nexus-matcher

Then merge the adapter, convert it (e.g. to GGUF for llama.cpp) and serve it as "nexus-matcher" on 127.0.0.1; see
README.md. The model replaces Claude Haiku 5.5 only if evaluate.py says so (exact match tie or better, >= 99% valid JSON).
"""

import argparse
import json
import pathlib

BASE_MODEL = "Qwen/Qwen2.5-1.5B-Instruct"


def load(path):
    return [json.loads(line) for line in pathlib.Path(path).read_text(encoding="utf-8").splitlines() if line]


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--data", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--base", default=BASE_MODEL)
    parser.add_argument("--epochs", type=float, default=3.0)
    args = parser.parse_args()

    # Imported here so the dataset tools and the tests do not need these heavy packages.
    from datasets import Dataset
    from peft import LoraConfig
    from trl import SFTConfig, SFTTrainer

    data = pathlib.Path(args.data)
    train = Dataset.from_list([{"messages": row["messages"]} for row in load(data / "train.jsonl")])
    peft_config = LoraConfig(r=16, lora_alpha=32, lora_dropout=0.05, task_type="CAUSAL_LM",
                             target_modules=["q_proj", "k_proj", "v_proj", "o_proj"])
    config = SFTConfig(
        output_dir=args.out,
        num_train_epochs=args.epochs,
        per_device_train_batch_size=4,
        gradient_accumulation_steps=4,
        learning_rate=2e-4,
        lr_scheduler_type="cosine",
        warmup_ratio=0.05,
        logging_steps=10,
        save_strategy="epoch",
        assistant_only_loss=True,
        seed=42,
    )
    trainer = SFTTrainer(model=args.base, train_dataset=train, args=config, peft_config=peft_config)
    trainer.train()
    trainer.save_model(args.out)
    print(f"LoRA adapter saved to {args.out}")


if __name__ == "__main__":
    main()
