# Exact-name organization experiments

See the [full report](../../docs/evaluations/2026-10-02-named-organization.md) for
results and limitations, and the [harness guide](../../docs/offline-evaluation.md)
for execution. This bundle is evaluation-only; it does not change worker defaults.

- `luna`, `sol6`, `sol61`: same long policy, exact names, three model settings.
- `concise-luna`, `concise-sol6`: rejected shorter-policy comparison.
- `hybrid-sol6`: selected long policy plus primary-certificate date clarification.
- `selected-repeat`: identical selected policy, independent calls.
- `fresh-sol6`, `fresh-luna`: frozen selected policy, ten reserved fresh documents.
- `boundary-sol6`, `boundary-luna`: eight post-hoc synthetic diagnostics.

[Development plan](plan.json), [concise plan](concise-plan.json),
[hybrid plan](hybrid-plan.json), [selection](selection.json),
[fresh validation plan](fresh-plan.json), and [boundary plan](boundary-plan.json)
record the sequential hypotheses. [Results](results.json) contain aggregate
real-document scores; [repeat agreement](repeat-agreement.json) excludes wording
differences. [Usage](usage.json) records every arm, including diagnostics.
[Boundary scores](boundary-results.json) contain only synthetic-case results.

The [synthetic cases](synthetic-boundaries.jsonl) can be reproduced without private
documents. Authenticate Codex with the existing ChatGPT setup first:

```powershell
dotnet run --project src/PaperlessLlm.Eval -- experiment `
  --cases eval/named-organization-2026-10-02/synthetic-boundaries.jsonl `
  --split holdout --recipe eval/named-organization-2026-10-02/boundary-sol6/recipe.json `
  --out C:\private\named-boundary-run --concurrency 4 --model gpt-6-sol --timeout 180
dotnet run --project src/PaperlessLlm.Eval -- score `
  eval/named-organization-2026-10-02/synthetic-boundaries.jsonl `
  C:\private\named-boundary-run --split holdout --version named-boundary-sol6 `
  --model gpt-6-sol-low --harness codex-named-text --out C:\private\boundary-score.json
```

Use a fresh private output directory per run. The schema is supplied in the
prompt, not enforced through an API structured-output parameter. Deterministic
conversion and validation reject bad proposals afterward. Raw responses remain
in per-case `final.txt`; conversion failures are case errors, counted as missing
converted inputs by the scorer. No Paperless credentials reach inference.

Private real-document cases and labels are deliberately omitted. Challenge
conditions repeat sources and are not independent observations. Existing tags
are nonexhaustive recovery targets, and type labels can encode subjective user
conventions. Do not interpret aggregate scores as overall tagging precision or a
production acceptance certificate.
