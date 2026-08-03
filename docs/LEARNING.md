# Correction memory and external fine-tuning

## What the app does (in-product)

Small models often repeat the same mistakes. AI IT-Company stores **lessons** (WRONG → RIGHT) and injects them into prompts for:

- ErrorFixer
- Backend / Frontend / Fullstack / GameCoder
- Architect

Sources:

1. **Agent → Teach** panel
2. **Review → Reject** optional teach form
3. Settings → **Learning** tab (list, enable/disable, delete)

Settings:

- Inject lessons into agent prompts (on/off)
- Max lessons in prompt (default 6)

Optional **Create Ollama model from lessons** builds a Modelfile:

```text
FROM <base>
SYSTEM """
…aggregated RIGHT rules…
"""
```

via `POST /api/create`. Assign `aiit-learned` (or your name) on the Agents page. This is **not** LoRA — it bakes the system prompt into a named model.

## Export for real fine-tuning / LoRA

**Export JSONL** writes to:

`%USERPROFILE%\AiItCompany\Learning\lessons-*.jsonl`

Each line:

```json
{"instruction":"...","input":"...","output":"...","role":"ErrorFixer","kind":"BuildError"}
```

Typical external flow:

1. Collect dozens/hundreds of clean lessons.
2. Convert with the helper script:

```bash
python scripts/lora_from_lessons.py -o train-lessons.jsonl
```

3. Train LoRA with Unsloth / Axolotl / similar on a GPU machine (chat messages format).
4. Export GGUF and `ollama create` / import into Ollama.
5. Point ErrorFixer (or all coders) at the new model in Agents.

The WinUI app does **not** run GPU training itself.

### Web search (opt-in)

Settings → Providers → **Web search**: enable + Tavily or Brave API key. Only **Architect** and **ErrorFixer** receive up to 3 budgeted snippets. Coders never call the search API.

### GitHub draft comment (opt-in)

Settings → Freelance → **GitHub: post draft comment on Accept**. Requires PAT. Off by default — Accept never posts without this flag (simulation mode also skips).
