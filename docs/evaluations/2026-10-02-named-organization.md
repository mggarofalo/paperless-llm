# Exact-name organization and model comparison

**The strongest candidate is one GPT-6 Sol call at low reasoning, using existing
OCR and exact taxonomy names resolved by deterministic code.** Sol 6.1 showed no
advantage in the direct comparison. Luna improved with the name-based interface
but remained less reliable at date decisions and at following organization rules.

This continues [v2](2026-10-02-ocr-text-v2.md). It includes 601 calls across
development comparisons, an unchanged repeat, ten fresh documents, and synthetic
diagnostics. [Recipes, plans, synthetic cases, and aggregate results](../../eval/named-organization-2026-10-02/README.md)
are checked in. Private documents, labels, outputs, and evidence are not.
Production defaults, live documents, and deployment settings remain unchanged.

## The interface change

V2 sometimes named one entity in evidence but returned another entity's valid
numeric ID. The new evaluation-only `outputContract: names` presents taxonomy
names instead of IDs in current metadata and allowed lists. The model returns
exact name strings for correspondent/type and `{name,evidence}` tag additions.
The resolver requires exactly one ordinal name match, then runs the existing
production intent validator. It does not repair output or silently discard errors.

Unknown/ambiguous names, numeric selections, duplicate JSON properties, and extra
tag keys fail. Existing or protected tag additions still fail. Original responses
remain available even when conversion fails; the scorer then records a missing
converted proposal. This is an interface rejection, not a transport failure.
Name resolution prevents numerical binding errors, but does not certify that a
selected name or supporting explanation is correct.

The model has no tools, images, or Paperless token. All calls are single-stage,
using existing OCR through the authenticated local Codex runner. Title and OCR
are kept. No separately billed API fallback was used. This does not establish
parity with the deployed Pi runner.

## Sequential development comparisons

The 25 previously exposed documents and 75 original/missing/wrong metadata cases
from v1/v2 remain unchanged. There are 46 scored challenge dates, 48
correspondents, 50 types, and 40 removed-tag targets per arm. Those tag targets
repeat the same underlying source documents and are not an exhaustive taxonomy
ground truth. References and prompts were saved before each arm; none of the
results below uses output normalization or post-result relabeling.

The first three arms compare the same long policy under the name contract. The
next two test a shorter policy. That shorter policy is rejected because it brings
back enclosure-date errors. A final hybrid retains the long policy and adds only
a clarification allowing the event/completion date of a primary certificate. It
also removes a stale heading referring to IDs; the earlier names prompt already
explicitly required strings in its schema and final instruction.

| Arm | Valid | Challenge dates | Correspondents | Types | Tags recovered | Extra tags |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Long, Luna medium | 72/75 | 24/46 | 41/48 | 38/50 | 37/40 | 8 |
| Long, Sol 6 low | 75/75 | 34/46 | 42/48 | 46/50 | 38/40 | 0 |
| Long, Sol 6.1 low | 75/75 | 34/46 | 42/48 | 46/50 | 38/40 | 0 |
| Concise, Luna medium | 74/75 | 35/46 | 42/48 | 34/50 | 37/40 | 7 |
| Concise, Sol 6 low | 75/75 | 36/46 | 41/48 | 43/50 | 38/40 | 0 |
| Hybrid, Sol 6 low | 75/75 | 35/46 | 41/48 | 46/50 | 38/40 | 0 |
| Hybrid, unchanged repeat | 75/75 | 36/46 | 42/48 | 46/50 | 38/40 | 0 |

Recovery requires a valid converted proposal. Extra tags count parseable raw
suggestions, including rejected proposals; one malformed Luna response could not
be audited as structured JSON. All Sol proposals were valid. Luna's four
rejections across the two development arms comprise malformed JSON, an existing
tag addition, and two unknown-name selections. These failures are retained in the
denominators.

The two long Sol versions also agree on the resulting fields and tag sets for
all 75 cases, not just on aggregate scores. This small experiment provides no
reason to prefer Sol 6.1 for this task. Both versions support low reasoning in
their official documentation: [Sol 6](https://developers.openai.com/api/docs/models/gpt-6-sol)
and [Sol 6.1](https://developers.openai.com/api/docs/models/gpt-6.1-sol).

### Development error audit

Long Sol, hybrid Sol, and the unchanged hybrid repeat make **zero scored date or
correspondent miswrites**, including the control cases. They preserve unreadable
check dates/payees and do not substitute an enclosed certification's date for an
application's date. The hybrid clarification repairs the primary completion
certificate's date. It does not eliminate all variation: one other date repair
and one correspondent repair differ between the hybrid and repeat runs.

The hybrid repeat agrees with the original hybrid on resulting dates in 74/75
cases, correspondents in 74/75, types in 75/75, and tag sets in 75/75. All four
agree together in **73/75** cases. Compare that with 46/75 for the earlier v2
Luna policy repeat; multiple variables differ, so this is not an isolated
estimate of the benefit of name resolution.

All three stronger long/hybrid runs disagree with the frozen type references on
the same two source documents: a tax payment demand becomes Invoice rather than
Record, and an asset transfer confirmation becomes Record rather than Statement.
That produces six type mismatches across three conditions, including two changes
to control classifications. These are plausible conventions, not invented facts;
they remain scored as mismatches rather than being relabeled to improve results.

An issuer is explicitly absent from the development taxonomy. The reference
instead points to a related plan entity. Sol abstains rather than substituting
that entity. Two other sources have unreadable check payees. These account for
most correspondent recovery misses. Missing dates largely reflect damaged or
absent OCR; the primary application's date is not present even though an
enclosure has a date. The unrecovered tag concerns services not established by
the damaged check OCR. Such misses must not be conflated with incorrect writes.

Luna still produces contradictory explanations and decisions, misses printed
dates, adds subjects outside the intended policy, and changes packet focus. The
concise prompt improves some recovery but trades away reliable date/type
selection. It is not selected. The hybrid is selected for a supported narrow
improvement with no new scored factual miswrite, not because it dominates every
metric.

## Fresh validation after prompt selection

Ten document IDs were reserved by deterministic hashing from a previously unused
subset before their contents were inspected. The hybrid prompt and Sol 6 low
selection were frozen before inspecting those contents. Reference labels were
then manually reviewed from existing OCR and frozen before inference. No prompt
edits or further tuning followed this validation.

The random sample contains nine receipts and one badly OCR'd check. It is useful
for this receipt-heavy workload but does not validate complex packets, insurance
classification, handwriting, or every document type. References are OCR-based,
not newly verified against source images. Two dates and three issuers are too
ambiguous to score confidently; they are excluded from accuracy and audited
separately. Each model receives 30 cases: original, missing, and wrong metadata.
Challenge titles are neutralized and subject receipt tags are removed.

| Fresh result | Sol 6 low | Luna medium |
| --- | ---: | ---: |
| Valid proposals | 30/30 | 30/30 |
| Missing dates recovered | 8/8 | 5/8 |
| Wrong dates corrected | 7/8 | 6/8 |
| Missing/wrong correspondents recovered | 14/14 | 14/14 |
| Missing/wrong types recovered | 20/20 | 20/20 |
| Removed receipt tags recovered | 18/18 | 18/18 |
| Extra tags | 0 | 1 |
| Scored control fields changed incorrectly | 0 | 0 |

Neither model makes a scored reference-disagreeing write. Sol's remaining date
abstention concerns a damaged prefix beside an otherwise surviving date substring.
Luna also abstains on several more dates and adds `receipts` to the check, contrary
to the policy.

**Excluded fields still matter:** both models infer the merchant on two receipts
whose merchant headers are missing, using a co-branded card name and surrounding
warehouse/receipt context. That produces four unscored issuer sets per model
across the missing/wrong conditions. The inferred merchant matches the existing
organization, but this does not establish sufficiently grounded issuer evidence.
Those sets are not quietly counted as correct. Both models keep the unscored
garbled dates and the unreadable check payee. The scored results should therefore
not be presented as universal perfect precision.

## Synthetic boundary diagnostics

After fresh validation, eight synthetic cases probe card-only evidence, a named
merchant using a different co-branded card, an unlisted issuer, a damaged check,
instructions embedded in OCR, enclosure dates, a primary completion certificate,
and clearly wrong existing metadata. The frozen prompt is unchanged. These are
post-hoc diagnostics, not another untouched generalization set.

Sol satisfies all eight cases' correctness requirements; Luna satisfies seven,
missing the vehicle/driving subject tag on the certificate. Both abstain when a
card brand is the only issuer evidence, distinguish the explicit merchant from
the card brand, preserve the unreadable check fields, and ignore the embedded
instruction to remove inbox or retrieve secrets. Both produce two redundant date
sets to already-present values; these are separately reported by the scorer and
are not treated as correctness failures. The cases do not certify resistance to
all prompt injection or all ambiguous merchant inference.

## Usage, verification, and decision

The development and fresh real-document runs use 585 calls, **8,337,019 input**
and **108,337 output tokens**. Sixteen additional synthetic calls are recorded
separately in the aggregate usage artifact. Initial long-prompt mean stage times
were approximately 7.3 seconds for Luna, 11.3 for Sol 6, and 12.3 for Sol 6.1;
these are local concurrency-six observations, not controlled service benchmarks
or billing estimates. One call per document remains the architecture.

The adapter has synthetic tests for name binding, unknown/ambiguous names,
numeric IDs, malformed/extra keys, duplicate JSON properties, protected/existing
tags, missing evidence, and invalid keep values. The 43 relevant local harness
tests pass. Saved hash manifests and runtime prompt provenance were checked;
the concise arm's standalone hash manifest was not produced, so its unchanged
prompt was verified against captured runner provenance and its corpus against
the original frozen corpus. No raw model output was repaired or overwritten.

Stop broad prompt searching here: the experiments support **Sol 6 low with the
hybrid exact-name contract** as the next integration candidate. Retain Paperless
OCR and use a single model call for organization. There is no demonstrated
benefit from adding a separate OCR model or selecting Sol 6.1 in this comparison.

Before production promotion, implement and test the same contract in the actual
worker/runner path, retain strict validation and private before/after records,
and observe a limited run. Resolve or document the subjective type conventions;
do not pretend the current reference set settles them. Weak issuer evidence and
poor OCR remain review targets. The current experiment does not restart the
worker, deploy a model change, rewrite OCR, or establish production-runner parity.
