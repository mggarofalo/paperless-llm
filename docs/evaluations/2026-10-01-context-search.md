# Context-management search, 2026-10-01

Status: the 100-configuration search and frozen holdout comparison are complete.
No candidate passed production acceptance, and no candidate has been deployed.

This extends the [four-prompt pilot](2026-10-01-pilot.md) into a broader adaptive
search. The [public recipe bundle](../../eval/context-search-2026-10-01/README.md)
contains instructions, context controls, parent relationships and aggregate
results. Private documents, labels, outputs and detailed audits are excluded.

## Protocol

The corpus has 20 development documents and five fresh held-out documents: an
80/20 split of 25 real documents. The development documents contain 33 supplied
pages; the holdout contains 11. The former pilot's exposed holdout became
development data. The new holdout remained sealed until candidate selection.

Each configuration is first screened on eight development documents and two
counterfactual metadata cases made from those same source documents. The latter
replace selected current metadata with wrong or missing values; they are not
additional real documents. This makes blanket preservation distinguishable from
correct organization. Selected candidates then expand to all 20 development
documents plus the same two challenges.

The budget is 100 distinct configurations, not 100 model calls. Screening alone
requires 1,000 document/configuration evaluations; multi-stage pipelines require
additional calls. The search is batched and adaptive, not 100 sequential accepted
improvements. It combines 40 broad probes, 30 combinations, 20 error-driven
variants, and 10 ablations. Late error-driven variants test deterministic assembly
of isolated page transcriptions after manual audits found cross-page copying.

Tested controls include old OCR present/absent; full/shortlisted taxonomy;
instruction order; low/medium/high reasoning; image size, overlapping regions and
rotated margins; isolated transcription, pagewise work, evidence ledgers,
critique and independent drafts; source-only auxiliary context; and compressed
draft history. Prompts contain general instructions and synthetic examples,
never reference answers or private case-specific corrections.

References and screening inputs were frozen before model trials. An independent
source-image review supplied 164 selected text regions across the 20 real development
documents (175 references when the two counterfactual copies are included),
plus signed amounts, identifiers and text anchors. The expansion references were
frozen before expanded evaluation. These are partial references, not complete
gold transcriptions.

## Metrics and acceptance

The frozen screening composite is:

`100 × (0.40 × OCR quality + 0.25 × repair + 0.20 × metadata preservation + 0.15 × metadata correction)`

OCR quality combines 70% rich-reference quality and 30% legacy key-fact recall.
Rich quality averages selected-region word alignment, signed-amount recall,
identifier recall and anchor recall. For a document requiring repair, repair
credit requires a valid OCR replacement that improves its old score; credit is
scaled by the fraction of its remaining score gap closed. Keeping broken OCR
earns no repair credit. Invalid proposals receive no resulting-field credit.

Metadata preservation covers title, date, correspondent and type on the original
documents. The correction challenges cover date, correspondent and type.
Same-value `set` proposals are tracked separately from resulting-field correctness;
they are not evidence of an actual Paperless write. Tag checks, validator rejection,
exact case pass counts, omissions, latency and
recorded token usage are reported separately. When a set has no correction
challenge, the remaining weights are normalized by 0.85.

This score does **not** measure full transcription accuracy, every hallucination,
or all repeated text. In particular, high-scoring candidates omitted peripheral
text, changed identifiers, and copied a neighboring page's footer. A separate
post-audit regression gate therefore checks 18 visually verified facts on four
development documents. Identical repeated blocks require separate occurrences
on the correct page. Keeping OCR counts as abstention, not successful repair.
This additional gate does not modify the frozen primary scores and is itself
still only a partial check.

## Screening outcome

All 100 distinct configurations completed screening on 1,000 case/configuration
pairs. There were 983 validator-valid proposals; transport and model-output
failures remain in the results rather than being silently discarded. The 17 final
invalid cases comprise ten timeouts, one HTTP/WebSocket 403, three model JSON/schema
failures, and three validator-policy failures (two unknown taxonomy IDs and one
protected tag). Recovered transport failures are counted as valid in this final
ledger, while all retained attempts remain in expenditure.
Three controls are reported separately and are not part of the 100.

The highest primary screening score was 97.586 (r056), but that configuration
failed three of four post-audit OCR case gates. It was not selected merely for
winning the proxy. The late rotated-margin configuration r090 scored 86.755,
passed all scored metadata decisions, and matched six selected OCR checks in
two rewritten cases while keeping OCR in two others. Manual inspection still
found missing readable text and a changed word. This is evidence of improvement,
not a complete-OCR acceptance result.

An unchanged repeat of the simple r013 recipe scored 77.470 versus its original
81.941 on the same screening set, with an additional wrong date decision and a
failed OCR regression case. Configuration identity does not imply output stability.

## Expanded validation and selection

Each expanded run used all 20 development documents plus two correction challenges.
Scores are composites, not accuracy percentages. Metadata is the mean of scored
field correctness per real document; an invalid proposal receives zero credit.

| Configuration | Composite | Valid / 22 | Metadata | Correction challenges | Recorded stage calls |
|---|---:|---:|---:|---:|---:|
| Deployed prompt control | 58.306 | 20 | 0.7250 | 1.00 | 22 |
| r013: simple, high reasoning | 82.633 | 22 | 0.9875 | 1.00 | 22 |
| r052: conservative pagewise | 81.407 | 21 | 0.9375 | 1.00 | 57 |
| r061: aggressive high-score candidate | 92.035 | 22 | 0.9750 | 1.00 | 22 |
| r087: deterministic page assembly | 77.705 | 22 | 1.0000 | 1.00 | 57 |
| r090: assembly plus rotated margins | 77.299 | 22 | 0.9875 | 1.00 | 57 |

The deterministic all-keep control scored 50.398 on the expanded set; it made no
model calls and corrected neither metadata challenge. All expanded model runs
were fresh, so the original screening documents also received a second sample.
The explicit r013 screening repeat added a third sample for that recipe.

r052's invalid expanded proposal contained duplicate JSON fields. This depresses
all of that case's field scores; it should not be described as multiple independently
observed wrong metadata values. Its other issues included a missed type correction
and an unexpected tag. r013 and r090 each made one type error. r087's expanded
metadata was fully correct, but its earlier screen had a type error.

[Selection](../../eval/context-search-2026-10-01/selection.json) was frozen before
opening the fresh holdout. r087 was chosen as a **research candidate for metadata**:
it had complete valid-output coverage and the strongest expanded metadata result.
r090 consumed more image tokens and did not sustain its perfect screening metadata.
Neither passed strict OCR acceptance, so this choice does not authorize deployment.
The prompts, image strategy, and runner revision were frozen; only private image
mapping paths changed for the holdout. There were no subsequent prompt adjustments.

## Findings

Removing noisy old OCR and allocating more reasoning to independent visual
reading improved the original score substantially. Enlarged regions helped some
documents; increasing resolution alone was not consistently sufficient.
Extra critique or draft stages often cost more without a reliable gain.

The most important finding is the gap between proxy score and strict acceptance.
Scores near 96 did not prevent incorrect digits or missing text. Conservative
variants passed the audited checks mostly by retaining uncertain OCR. Aggressive
variants repaired more text but introduced false-complete replacements.

`pagewise-compose` tests a structural response: each fresh model call sees only
one original page, emits a validated page record, and a final model handles
metadata. Code assembles the original page records without letting that final
model rewrite their text. Any incomplete or uncertain page forces OCR keep.
Manual audit found a duplicated receipt line and altered identifiers even with
this approach. The simple and conservative finalists also failed source-image
review. Passing the 18 selected regression checks was insufficient: other
readable text still changed or disappeared.

This prevents assembly-time rewriting; it cannot prevent the page model itself
from confidently reading a glyph incorrectly.

## Untouched holdout

The five fresh documents contained 11 pages. Each recipe ran exactly once after
selection was frozen at 2026-10-02 00:56 UTC. No counterfactual correction cases
were added to the holdout, so its composite normalizes the remaining weights by
0.85 and is not directly comparable with the screening/expanded composite.

| Configuration | Composite | Valid / 5 | Metadata | OCR replacements | Recorded stage calls |
|---|---:|---:|---:|---:|---:|
| Deployed prompt control | 49.788 | 5 | 0.70 | 0 | 5 |
| Frozen r087 | 53.318 | 5 | 0.85 | 0 | 16 |

Both kept existing OCR on every holdout document and earned zero repair credit.
The candidate's better aggregate metadata score did not make it safer: source-image
review found a handwritten date misread that also changed a correct date in the
title. The baseline read that date correctly. The score advantage largely reflects
preserving existing descriptive titles; it is not evidence of superior classification
of new, unorganized documents. Interpretive taxonomy differences and harmless
rewrites are distinguished from factual errors in the manual audit. In particular,
the baseline's four title changes were supported by the documents, but unnecessary
under the title-preservation policy. A multi-document date/type disagreement selected
a real enclosed certification rather than the original application; a separate
Statement-to-Record change was a plausible taxonomy interpretation.

Manual review also found one legacy key-fact label with a wrong first digit; the
source image and separately frozen rich reference agreed with each other. Frozen
labels and scores were not changed after opening the holdout. This limits the
absolute OCR score, but it cannot explain away the directly observed date regression.
One holdout repair-required label also allowed an uncertainty marker for an unreadable
signature, whereas the production complete-OCR rule requires abstention. Zero repair
credit must therefore not be interpreted as five unsafe decisions. Both approaches
made safe OCR abstentions; the candidate still made the factual metadata regression.
All 11 holdout pages and both sets of five outputs received source-image review.

## Acceptance decision

No production prompt or worker behavior changes are recommended from this search.
The selected research candidate made a definite factual metadata regression on a
fresh handwritten date, propagating the misread into the document title. This is
not merely a disagreement over wording or taxonomy. The deployed-prompt control
read that date correctly. Development audits had already established false-complete
OCR replacements in the finalists.

The useful outcome is a reusable experiment harness and evidence about where the
current contract fails. Prompting and image geometry can improve selected outcomes,
but neither JSON validity nor the model's assertion of complete transcription is
an adequate accuracy check. More variations of these prompts are unlikely to be
the best next expenditure.

A follow-up should change and test the contract: retain existing OCR, protect
already populated factual metadata with deterministic rules, and evaluate a narrower
metadata proposal path before enabling writes. That is a proposed next step, not an
implemented or validated production configuration. The stopped worker should remain
stopped until a separately validated change is ready. No release was created.

## Expenditure and value

[Recorded usage](../../eval/context-search-2026-10-01/usage.json) totals **2,001
stage attempts, 38,138,478 input tokens and 1,596,427 output tokens** across
screening, controls, expanded comparisons, repeats, the holdout and retained
infrastructure reruns. Planning, labeling and audit agents are excluded. Calls
interrupted without saved provenance make this a lower bound; it is not a dollar
bill or an estimate of uncached input. Subscription authentication was used.

The broad search was useful for finding structural failure modes and demonstrating
that a high composite can hide harmful changes. Multi-stage recipes commonly used
more calls and tokens without dependable acceptance gains. Further prompt-only
search has diminishing value relative to a narrower task contract and deterministic
preservation safeguards.

## Execution and limitations

Experiments use local Codex CLI 0.159.2 and `gpt-6-luna`, with fresh sessions,
user configuration ignored, tools disabled and tool-event rejection. Production
uses the Pi bridge with different rendering and output limits. These results do
not establish production-runner parity.

A Windows stdin encoding bug initially broke Unicode-containing staged prompts.
Those attempts were archived and rerun after explicitly selecting UTF-8. Later
WebSocket failures and timeouts were separated from model JSON/validation errors.
Parallel launches were stopped, successful results retained, and unfinished
cases resumed at lower concurrency. Failed/retried stages remain part of the
recorded expenditure; incomplete process logs make that usage a lower bound.

The initial plan to expand ten candidates was narrowed after the manual failures
and a discussion of token value. Final validation focuses on a few structurally
different candidates rather than spending equally on similar high proxy scores.

The corpus is small and selected, metadata is mostly already curated, and the
counterfactual challenge set is small. Reference typography and ambiguous dates
or types can also disagree with an otherwise reasonable interpretation. Original
OCR lacks page boundaries, so its region matching is more permissive than
page-indexed replacement matching. No percentage here should be generalized to
the whole Paperless archive or to fresh document types.

The six documents changed by the earlier worker run were restored before these
experiments. The experiment harness has no Paperless write client. Production
prompt, worker configuration and documents are not modified by this search.
