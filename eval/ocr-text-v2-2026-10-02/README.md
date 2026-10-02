# Existing-OCR organization v2

Offline prompt experiment; these are not production defaults. See the
[results report](../../docs/evaluations/2026-10-02-ocr-text-v2.md) and
[evaluation harness guide](../../docs/offline-evaluation.md).

## Frozen arms

| Directory | Change from v1 |
| --- | --- |
| `baseline` | Unchanged v1 prompt, rerun contemporaneously |
| `contract` | Adds a complete no-change JSON template and serialization checklist |
| `policy` | Adds date evidence gates and provisional tag/type conventions to `contract` |
| `policy-repeat` | Identical policy prompt and settings, independent calls |

The [plan](plan.json) was frozen before inference. Each arm uses the same 75
cases from 25 previously exposed source documents: unchanged, missing-field,
and wrong-field conditions. OCR snapshots and reference answers are unchanged
from v1. No images, tools, Paperless writes, output normalization, or new OCR.
The policy conventions are hypotheses, not confirmed user preferences.

Private inputs, output proposals, evidence, and per-case scores are deliberately
excluded. [Aggregate results](results.json) include validator-gated field
scores, abstentions, reference-disagreeing writes, tag recovery, extra additions,
usage, and policy-repeat agreement. Tag targets are not exhaustive; extra tags
are not automatically false positives. Repeated cases are not independent
documents.

## Reproduce with a private corpus

Build `src/PaperlessLlm.Eval`, authenticate the local Codex CLI using the existing
ChatGPT setup, then run each arm into a new private output directory:

```powershell
dotnet run --project src/PaperlessLlm.Eval -- experiment `
  --cases C:\private\cases.jsonl --split train `
  --recipe eval/ocr-text-v2-2026-10-02/policy/recipe.json `
  --out C:\private\v2-policy --concurrency 8 --model gpt-6-luna --timeout 180
dotnet run --project src/PaperlessLlm.Eval -- score `
  C:\private\cases.jsonl C:\private\v2-policy --split train `
  --version ocr-text-v2-policy --model gpt-6-luna-medium `
  --harness codex-single-text --out C:\private\v2-policy-score.json
```

Expected answers and curator notes stay grader-only. Use no production Paperless
token in the inference process. Exact numerical reproduction is not guaranteed;
the repeat arm measures some of that variability. A fresh evaluation set and
deployment-runner validation are still needed before production acceptance.
