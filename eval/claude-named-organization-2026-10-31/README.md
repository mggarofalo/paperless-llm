# Claude model evaluation on the exact-name organization task

See the [full report](../../docs/evaluations/2026-10-31-claude-named-organization.md)
for results and limitations, and the [harness guide](../../docs/offline-evaluation.md#claude-code-harness)
for execution. This bundle is evaluation-only. It reuses the frozen selected
prompt (`DE1C8363…`) and the 30 reserved fresh holdout conditions from the
[October 2 comparison](../named-organization-2026-10-02/README.md) without
change, and runs them through the production Claude Code bridge.

- `fresh-haiku`, `fresh-sonnet`: frozen selected policy, ten reserved fresh
  documents in 30 original/missing/wrong conditions, `claude-haiku-5-5` and
  `claude-sonnet-5-5` at low effort.
- `boundary-haiku`, `boundary-sonnet`: the eight checked-in
  [synthetic boundary cases](../named-organization-2026-10-02/synthetic-boundaries.jsonl)
  through the same bridge.

[Plan](plan.json) records the arms, prompt hash and preregistered rules.
[Results](results.json) contain aggregate real-document scores in the same shape
as the earlier `fresh-sol6` arm, plus per-case agreement with that arm.
[Boundary scores](boundary-results.json) contain only synthetic-case results.
[Usage](usage.json) records calls, uncached and cached token counts and
serial latency for every arm, beside the Codex reference arms.

The boundary cases can be reproduced without private documents. The runner home
must hold a Claude subscription sign-in made by the pinned Claude Code CLI
(`claude auth login --claudeai`, or the worker's `auth login`):

```powershell
dotnet run --project src/PaperlessLlm.Eval -- experiment `
  --cases eval/named-organization-2026-10-02/synthetic-boundaries.jsonl `
  --split holdout --recipe eval/claude-named-organization-2026-10-31/boundary-sonnet/recipe.json `
  --out C:\private\claude-boundary-run --concurrency 1 --timeout 300 `
  --harness claude --runner-home C:\private\claude-auth --model claude-sonnet-5-5
dotnet run --project src/PaperlessLlm.Eval -- score `
  eval/named-organization-2026-10-02/synthetic-boundaries.jsonl `
  C:\private\claude-boundary-run --split holdout --version boundary-sonnet `
  --model claude-sonnet-5-5-low --harness claude-bridge-named --out C:\private\boundary-score.json
```

Each call is one `claude -p` run with the production bridge's flags: no tools
besides structured output, no MCP servers, settings or saved sessions, the
names-contract schema enforced through `--json-schema`, and low effort. The
recipe prompt is the system prompt; the document payload and schema text form
the user message. Deterministic name resolution and the production validator
reject bad proposals afterward; raw responses stay in each case's `final.txt`.
No Paperless credentials reach inference.

Private real-document cases, labels, outputs and evidence are deliberately
omitted. The fresh sample is receipt-heavy and small; challenge conditions
repeat ten source documents and are not independent observations. Do not read
aggregate scores as overall tagging precision or a production acceptance
certificate.
