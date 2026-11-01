# Claude models on the exact-name organization task

**Sonnet 5.5 at low effort matches Sol 6 low on the frozen held-out set and
stays the Claude harness default.** It produced 30 valid proposals with no
incorrect write, no extra tag and no scored control change, and agreed with Sol 6
on every field in 29 of 30 cases. Haiku 5.5 at low effort is also safe on this
set but abstains on more dates, missed one receipt tag, and is slower with three
times the output tokens; it is not recommended as the default.

This measures the Claude harness shipped in PPLLM-39 the way Sol 6 was measured
in the [October 2 comparison](2026-10-02-named-organization.md). The frozen
selected prompt (`DE1C8363…`) and the 30 reserved fresh holdout conditions are
unchanged; the eight synthetic boundary cases are the checked-in ones. 76 calls
ran through the **production Claude Code bridge**, one at a time, on the owner's
Windows machine with the owner's Claude subscription. [Recipes, plan, aggregate
results and usage](../../eval/claude-named-organization-2026-10-31/README.md)
are checked in. Private documents, labels, outputs and evidence are not. The
worker's default model, production prompt and deployment settings are unchanged.

## How the calls were made

`PaperlessLlm.Eval experiment --harness claude` runs `runner/claude-bridge.mjs
infer` for each case with the worker's environment allowlist, so the pinned
Claude Code 2.1.314 binary, its flags (`--tools ''`, `--strict-mcp-config`,
`--setting-sources ''`, `--no-session-persistence`, `--max-turns 3`,
`--json-schema`), the effort setting and the fail-closed checks are the ones
the deployed worker uses. The recipe prompt is the system prompt and the
document payload plus schema text is the user message, which is the same text
the Codex harness sends as one composed prompt. The schema is the names
contract enforced through structured output; it now carries the `decisions`
diagnostics block added after October 2, which is stripped before validation
and does not affect scoring. Deterministic name resolution and the unchanged
production validator judged every response afterward. No images, tools or
Paperless token were involved.

Unlike the Codex evaluation, which ran `codex exec` rather than the deployed Pi
runner, this run does exercise the production bridge file. It does not exercise
the Linux container image or its native binary, and it uses the frozen
experimental prompt rather than the current production prompt, which adds the
long-term note instructions and `schema_version` 2.

## Fresh holdout results

Same ten reserved documents, same frozen labels, 30 conditions (original,
missing, wrong) per model. Sol 6 and Luna columns are the October 2 results.

| Fresh result | Sol 6 low | Luna medium | Sonnet 5.5 low | Haiku 5.5 low |
| --- | ---: | ---: | ---: | ---: |
| Valid proposals | 30/30 | 30/30 | 30/30 | 30/30 |
| Missing dates recovered | 8/8 | 5/8 | 7/8 | 6/8 |
| Wrong dates corrected | 7/8 | 6/8 | 7/8 | 7/8 |
| Missing/wrong correspondents recovered | 14/14 | 14/14 | 14/14 | 14/14 |
| Missing/wrong types recovered | 20/20 | 20/20 | 20/20 | 20/20 |
| Removed receipt tags recovered | 18/18 | 18/18 | 18/18 | 17/18 |
| Extra tags | 0 | 1 | 0 | 0 |
| Scored control fields changed incorrectly | 0 | 0 | 0 | 0 |
| Incorrect writes in any condition | 0 | 0 | 0 | 0 |

Every Claude miss is an abstention, never a wrong value. Per case, Sonnet agrees
with Sol 6 on dates in 29/30, correspondents 30/30, types 30/30 and tag sets
30/30, so all fields agree in **29/30** cases. Haiku agrees on dates in 26/30 and
tag sets in 29/30, all fields in **25/30**.

### Error audit

Sonnet's two date abstentions are the same source document whose timestamp has
a garbled prefix beside an otherwise surviving date substring. Sol 6 abstained
on one of its two conditions and set the other; Sonnet abstained on both and
said why. Haiku abstained on that document once and set it once, correctly, in
the condition where Sol abstained. Haiku's two other abstentions are a receipt
whose unlabeled `07/03/26` it read as either July 3 or March 7; Sol 6, Sonnet and
the frozen label treat it as a US month-first date. That is over-caution, not a
miswrite, but it leaves a recoverable field missing. Haiku's missed tag is a
liquor receipt in the wrong-metadata condition on which it added nothing; its
explanation discusses incidental tax and membership lines and never mentions the
receipt itself.

**Excluded fields still matter.** Exactly as Sol 6 and Luna did, both Claude
models infer the warehouse merchant on the two receipts whose merchant headers
are missing, using the co-branded card name, member number and warehouse
context. That produces four unscored issuer sets per model. The inferred
merchant matches the existing organization, but weak issuer evidence remains a
review target, and these sets are not counted as correct.

## Synthetic boundary diagnostics

The frozen prompt and the eight checked-in cases are unchanged. Both Claude
models satisfy seven of eight correctness requirements. Sonnet misses the
vehicle/driving subject tag on the driver-course certificate, the same miss
Luna made, reasoning that a training course is not vehicle ownership or
service. Haiku misses the `receipts` tag on the embedded-command receipt while
correctly ignoring the instruction in its OCR to choose a different issuer,
remove inbox and fetch secrets. Both abstain when a card brand is the only
issuer evidence, pick the explicit merchant over the card brand, preserve the
unreadable check fields, and refuse to substitute a listed entity for an
unlisted issuer. Haiku produced one redundant date set to an already-present
value (Sol 6 and Luna produced two each); Sonnet produced none. The scorer
reports these separately and they are not correctness failures.

## Latency, usage and plan consumption

Calls ran serially (`--concurrency 1`), which is how the worker calls the
bridge. Times are wall-clock per bridge process on one Windows machine over a
residential connection, not service benchmarks.

| Arm | Calls | Mean s | Median s | Max s | Cached input read | Cached input written | Uncached input | Output |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| fresh-sonnet | 30 | 5.21 | 5.17 | 10.53 | 141,516 | 105,774 | 64 | 17,934 |
| fresh-haiku | 30 | 8.57 | 8.45 | 12.63 | 196,692 | 117,951 | 76 | 51,854 |
| boundary-sonnet | 8 | 4.59 | 4.61 | 5.31 | 31,185 | 21,143 | 16 | 4,144 |
| boundary-haiku | 8 | 8.85 | 8.93 | 11.36 | 50,891 | 25,212 | 22 | 13,302 |

Sol 6's fresh arm averaged 11.35 seconds per call at concurrency six through
`codex exec`, so the two latencies are not directly comparable. Nearly all
Claude input is served from the prompt cache because the policy prompt is the
same across cases; Haiku's extra input reflects a different cache split, and
its output is about three times Sonnet's because it writes longer evidence.
Across the 76 recorded calls there were no timeouts, truncations, schema or
validator failures, and no `rate_limited` or `auth_required` results.

The bridge forwards token counts but not Claude Code's cost figure, and the
subscription's usage meter is not exposed through a documented interface, so the
share of the plan consumed was not measured. The 76 calls, about 690,000 cached
and uncached input tokens and 87,000 output tokens, did not trigger a rate limit
during roughly twelve minutes of serial use.

One earlier batch of 68 calls was refused by the bridge before it read any
request: the bridge exited within 11 seconds for every case while a bridge
status check and a direct inference succeeded minutes later. The harness at
that point recorded only a broken-pipe exception, so the cause is not known;
it now records the bridge's own error code. Those arms were rerun from empty
directories and nothing from the refused batch was kept. The deployed worker
treats such a refusal as a pause without consuming job attempts.

## Verification and limitations

Case, prompt and recipe hashes were saved before the runs and match the October
2 frozen artifacts. No prompt edit, relabeling, output repair or result
selection followed the runs. The harness has tests with a fake bridge covering
the request shape, the environment allowlist, the prompt split, bridge error
codes, stdin refusal, structured-result rejection and recipe validation; the
full .NET suite, the complexity gate and the JavaScript tests pass.

The fresh sample is small and receipt-heavy, with ten source documents repeated
across conditions, so these are not independent observations and do not
validate complex packets, insurance classification, handwriting or every
document type. References are OCR-based, not scan-verified. One run per model
measures no repeat variability. Low effort was the only setting tested; medium
effort, Opus or images were not tried. Equal scores on this set do not
establish that the models behave identically on other libraries.

## Decision

Keep `claude-sonnet-5-5` at low effort as the Claude harness default. The README
no longer describes it as untested on the private evaluation. Haiku 5.5 is a
reasonable explicit choice for an operator who wants to conserve plan usage and
accepts more date abstentions, but it is not the default. The Codex default,
Sol 6 low, is unchanged. Before promoting any Claude deployment, observe a
limited run with `needs review` as the October 2 note recommends; this
evaluation does not replace that step.
