"""LoRA fine-tuning of a small open model as the Recipe Matcher (references/ARQUITETURA.md, section 10.1).

Trains on train.jsonl: the chat template of the system prompt + recipe catalog and the request is the prompt, the expected
JSON is the answer, and the loss is computed only on the answer (prompt tokens are masked). Plain transformers Trainer +
PEFT, so it runs on older GPUs too: on a GPU with less than 10 GB (e.g. a 6 GB GTX 1660) it loads the base model in 4 bits
(QLoRA); bf16 only where the GPU has it natively (compute capability 8.0+), fp16 otherwise. Needs the packages in
requirements-train.txt; it is not run by the test suite.

    pip install -r tools/finetune/requirements-train.txt
    python tools/finetune/train_lora.py --data tools/finetune/data --out <dir>/nexus-matcher

The adapter is saved in --out and, unless --no-merge, the base model with the adapter merged in fp16 in --out/merged,
ready for serve_local.py. The model replaces Claude Haiku 5.5 only if evaluate.py says so (exact match tie or better,
>= 99% valid JSON).
"""

import argparse
import json
import pathlib

BASE_MODEL = "Qwen/Qwen2.5-1.5B-Instruct"
IGNORE = -100


def load(path):
    return [json.loads(line) for line in pathlib.Path(path).read_text(encoding="utf-8").splitlines() if line]


def encode(tokenizer, messages, max_length):
    """Token ids of the whole conversation and labels that keep only the assistant answer."""
    assert [m["role"] for m in messages] == ["system", "user", "assistant"]
    prompt = tokenizer.apply_chat_template(messages[:2], tokenize=False, add_generation_prompt=True)
    full = tokenizer.apply_chat_template(messages, tokenize=False)
    assert full.startswith(prompt), "the chat template must render the prompt as a prefix of the conversation"
    prompt_ids = tokenizer(prompt, add_special_tokens=False)["input_ids"]
    full_ids = tokenizer(full, add_special_tokens=False)["input_ids"][:max_length]
    labels = [IGNORE] * min(len(prompt_ids), len(full_ids)) + full_ids[len(prompt_ids):]
    return {"input_ids": full_ids, "attention_mask": [1] * len(full_ids), "labels": labels}


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--data", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--base", default=BASE_MODEL)
    parser.add_argument("--epochs", type=float, default=3.0)
    parser.add_argument("--no-4bit", dest="four_bit", action="store_false")
    parser.add_argument("--no-merge", dest="merge", action="store_false")
    parser.add_argument("--max-length", type=int, default=1024)
    parser.add_argument("--batch-size", type=int, default=1)
    parser.add_argument("--grad-accum", type=int, default=16)
    args = parser.parse_args()

    # Imported here so the dataset tools and the tests do not need these heavy packages.
    import torch
    from datasets import Dataset
    from peft import LoraConfig, get_peft_model
    from transformers import (AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig, DataCollatorForSeq2Seq, Trainer,
                              TrainingArguments)

    if not torch.cuda.is_available():
        raise SystemExit("A CUDA GPU is required for training.")
    # is_bf16_supported() is also true where bf16 is only emulated (Turing); use it only where it is native.
    bf16 = torch.cuda.get_device_capability(0)[0] >= 8
    dtype = torch.bfloat16 if bf16 else torch.float16

    tokenizer = AutoTokenizer.from_pretrained(args.base)
    if tokenizer.pad_token is None:
        tokenizer.pad_token = tokenizer.eos_token
    rows = load(pathlib.Path(args.data) / "train.jsonl")
    train = Dataset.from_list([encode(tokenizer, row["messages"], args.max_length) for row in rows])
    answer_tokens = sum(sum(1 for t in r["labels"] if t != IGNORE) for r in train)
    print(f"{len(train)} examples, {answer_tokens} answer tokens, longest {max(len(r['input_ids']) for r in train)} tokens")

    quantization = BitsAndBytesConfig(load_in_4bit=True, bnb_4bit_quant_type="nf4", bnb_4bit_use_double_quant=True,
                                      bnb_4bit_compute_dtype=dtype) if args.four_bit else None
    model = AutoModelForCausalLM.from_pretrained(args.base, dtype=dtype, quantization_config=quantization, device_map={"": 0})
    if args.four_bit:
        from peft import prepare_model_for_kbit_training
        model = prepare_model_for_kbit_training(model, use_gradient_checkpointing=True)
    else:
        model.gradient_checkpointing_enable()
        model.enable_input_require_grads()
    model = get_peft_model(model, LoraConfig(r=16, lora_alpha=32, lora_dropout=0.05, task_type="CAUSAL_LM",
                                             target_modules=["q_proj", "k_proj", "v_proj", "o_proj"]))
    model.print_trainable_parameters()

    config = TrainingArguments(
        output_dir=args.out,
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch_size,
        gradient_accumulation_steps=args.grad_accum,
        learning_rate=2e-4,
        lr_scheduler_type="cosine",
        warmup_steps=4,  # about 5% of the 75 steps (400 examples, 3 epochs, 16 per step)
        logging_steps=5,
        save_strategy="no",
        bf16=bf16,
        fp16=not bf16,
        report_to=[],
        remove_unused_columns=False,
        seed=42,
    )
    collator = DataCollatorForSeq2Seq(tokenizer, padding=True, label_pad_token_id=IGNORE)
    trainer = Trainer(model=model, args=config, train_dataset=train, data_collator=collator)
    trainer.train()
    model.save_pretrained(args.out)
    tokenizer.save_pretrained(args.out)
    print(f"LoRA adapter saved to {args.out}")

    if args.merge:
        # Merge on fp16 weights (a 4-bit model cannot be merged exactly): reload the base, apply the adapter, save.
        from peft import PeftModel
        del trainer, model
        torch.cuda.empty_cache()
        base = AutoModelForCausalLM.from_pretrained(args.base, dtype=torch.float16, device_map={"": "cpu"})
        merged = PeftModel.from_pretrained(base, args.out).merge_and_unload()
        target = pathlib.Path(args.out) / "merged"
        merged.save_pretrained(target)
        tokenizer.save_pretrained(target)
        print(f"Merged model saved to {target}")


if __name__ == "__main__":
    main()
