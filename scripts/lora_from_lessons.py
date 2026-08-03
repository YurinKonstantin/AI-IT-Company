#!/usr/bin/env python3
"""Convert AI IT-Company correction lessons JSONL to Axolotl/Unsloth chat format.

Usage:
  python scripts/lora_from_lessons.py "%USERPROFILE%\\AiItCompany\\Learning\\lessons-*.jsonl" -o train.jsonl

Then train externally (example Unsloth) and import GGUF into Ollama.
See docs/LEARNING.md.
"""
from __future__ import annotations

import argparse
import glob
import json
import sys
from pathlib import Path


def load_lines(patterns: list[str]) -> list[dict]:
    rows: list[dict] = []
    for pat in patterns:
        for path in glob.glob(pat, recursive=False):
            with open(path, encoding="utf-8") as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        rows.append(json.loads(line))
                    except json.JSONDecodeError as e:
                        print(f"skip bad line in {path}: {e}", file=sys.stderr)
    return rows


def to_chat(row: dict) -> dict:
    instruction = (row.get("instruction") or "").strip()
    inp = (row.get("input") or "").strip()
    output = (row.get("output") or "").strip()
    role = row.get("role") or "Any"
    kind = row.get("kind") or ""

    user = instruction
    if inp:
        user = f"{instruction}\n\nContext:\n{inp}" if instruction else inp
    if not user:
        user = f"Fix this ({role}/{kind})."

    system = (
        f"You are a careful C# / .NET / WinUI / MonoGame assistant "
        f"(lesson role={role}, kind={kind}). Prefer minimal correct edits."
    )
    return {
        "messages": [
            {"role": "system", "content": system},
            {"role": "user", "content": user},
            {"role": "assistant", "content": output or "(empty)"},
        ]
    }


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument(
        "inputs",
        nargs="*",
        help="JSONL paths or globs (default: %%USERPROFILE%%/AiItCompany/Learning/lessons-*.jsonl)",
    )
    p.add_argument("-o", "--output", default="train-lessons.jsonl")
    p.add_argument("--min", type=int, default=1, help="Minimum lessons required")
    args = p.parse_args()

    patterns = args.inputs
    if not patterns:
        home = Path.home() / "AiItCompany" / "Learning"
        patterns = [str(home / "lessons-*.jsonl")]

    rows = load_lines(patterns)
    if len(rows) < args.min:
        print(f"Need at least {args.min} lessons, found {len(rows)}", file=sys.stderr)
        return 1

    out = Path(args.output)
    with out.open("w", encoding="utf-8") as f:
        for row in rows:
            if not (row.get("output") or "").strip():
                continue
            f.write(json.dumps(to_chat(row), ensure_ascii=False) + "\n")

    print(f"Wrote {out} from {len(rows)} source rows")
    print("Next: train LoRA (Unsloth/Axolotl) → export GGUF → ollama create → Agents page")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
