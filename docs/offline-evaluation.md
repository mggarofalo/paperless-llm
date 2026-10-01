# Offline intent evaluation

## Authenticated prompt experiments

Run prompt recipes through the .NET `experiment` command. Every stage starts a
fresh local Codex process in a temporary working directory with read-only
sandboxing and user configuration ignored. Stage event streams, text, errors,
and provenance are written to the chosen output directory. Treat that directory
as sensitive because it can contain document-derived text. Expected answers and
curator notes are never sent to Luna.

```powershell
dotnet run --project src/PaperlessLlm.Eval -- experiment `
  --cases C:\private\eval\cases.jsonl --split train --recipe C:\private\eval\recipe.json `
  --out C:\private\eval\runs\recipe-id --concurrency 6 --model gpt-6-luna
dotnet run --project src/PaperlessLlm.Eval -- score `
  C:\private\eval\cases.jsonl C:\private\eval\runs\recipe-id --split train `
  --version recipe-id --out C:\private\eval\score.json --references C:\private\eval\references.json `
  --regressions C:\private\eval\manual-regression-references.json
```

Recipes require `id` and `promptFile`. Optional fields are `parent`,
`hypothesis`, `auxiliaryPromptFile`, `pipeline` (`single`, `ocr-first`,
`pagewise`, `pagewise-compose`, `refine`, `ledger`, `dual`), `reasoning` (`low`, `medium`, `high`),
`ocrContext` (`full`, `none`), `auxiliaryContext` (`full`, `images-only`),
`draftContext` (`all`, `latest`), `taxonomy` (`full`, `shortlist`), `contextOrder`
(`instructions-first`, `evidence-first`), `imageMode` (`full`, `regions`,
`full-and-regions`, `high`), `imageVariants` (recipe-relative mapping file),
and `includeFinalImages`.

`experiment --split train|holdout` is required. The runner filters the case file
to that split before any model call, so a train run cannot send holdout cases.
Each `--out` directory must be new or empty; the runner refuses to reuse an
existing run directory so stale candidate files cannot hide failed cases.

The image mapping is keyed by case ID. Each case can contain `full`, `high`,
`regions`, and `full-and-regions` arrays plus `pages` entries with a 1-based
`page`, `full` image, optional `high` image, and `regions` array. Pagewise mode checks
the page count and numbering before making a fresh transcription call for each
page. `pagewise-compose` validates a separate `{page,text,complete,uncertainty}`
record for each page in an isolated image-only call. Its final model stage handles
metadata and is forced to keep OCR; the runner composes OCR from the validated
page records only when every page is complete and uncertainty-free. Otherwise
the candidate keeps OCR and records the page-level uncertainty. Shortlist mode
uses deterministic lexical overlap and retains original
taxonomy IDs. `auxiliaryContext: images-only` withholds metadata, OCR, and
taxonomy from OCR, ledger, and pagewise stages while retaining explicit image
anchors. `draftContext: latest` includes only the latest synthesis from prior
stages in the final call. Prior-stage drafts are labeled untrusted evidence.
`--timeout` sets a per-stage limit in seconds (default 300, allowed
10..1800). Failed cases remain missing from scorer-compatible `caseId.json`
outputs and receive an `.error.txt` record. The scorer remains the authority on
schema and production-validator behavior.
When `--references` is supplied to `score`, an additional `.regions.json` file
reports partial OCR-region, signed-amount, identifier, and anchor metrics against
the private reference map without changing the existing report schema.
When `--regressions` is supplied, `.regressions.json` runs the post-audit partial
reference gate on the selected split. It checks validated OCR `set` output on each
reference page, counts repeated checks independently, and reports zero-based
`missingCheckIndices` without copying private check text or notes. OCR `keep` is
reported as a safe abstention; malformed or production-validator-rejected output
is reported separately as invalid. This gate measures only its reviewed partial
checks and does not certify complete transcription.

The export and score commands in `PaperlessLlm.Eval` remain offline: they do not use a Paperless client or authentication. The experiment command separately invokes the authenticated local Codex CLI as described above. The harness references the production `IntentPrompt.Build`, `DocumentIntent.Schema`, and `IntentValidator` directly. It does not alter the production prompt.

Keep the real case file, page images, task exports, model outputs, and reports in a private directory outside this repository. Do not commit them. `notes` and all `expected` labels are grader-only: export includes only the production prompt, schema, input payload, case ID, split, and page image paths. Page image paths are references and are not opened or copied by the harness.

## Case file

Use UTF-8 JSON Lines, one object per case. Property names use camel case. The following is a synthetic example:

```json
{"caseId":"sample-01","split":"train","pageCount":1,"document":{"id":1,"title":"Acme receipt","content":"OCR excerpt","created":"2026-05-02","correspondentId":null,"documentTypeId":null,"tags":[2],"pageImages":["C:/private/scans/sample-01-1.png"]},"taxonomy":{"tags":[{"id":2,"name":"inbox","isInboxTag":true},{"id":3,"name":"receipts"}],"correspondents":[{"id":4,"name":"Acme"}],"documentTypes":[{"id":5,"name":"Receipt"}]},"expected":{"title":{"action":"preserve"},"date":{"action":"set","value":"2026-05-02"},"correspondent":{"action":"set","value":4},"documentType":{"action":"set","value":5},"addTagIds":[3],"protectedTagIds":[2],"ocr":{"mustReplace":true,"keyFacts":["Acme","$15.00"],"forbiddenFacts":["$150.00"]},"critical":true},"notes":"Reference checked against source scan."}
```

`document` supplies the local document fields needed by the real prompt builder and validator. Its `pageImages` are passed through for an external vision runner. Set `pageCount` to the number of supplied pages, up to the production limit of 10. Taxonomy IDs and names must match the local reference set. Use IDs only from the case; no API lookup is performed.

Expected metadata actions are `set`, `keep`, or `ignore`; title also supports `preserve`. The expected action describes the reference state: `keep`/`preserve` means the current value, while `set` uses `value` as the reviewed reference. The candidate is graded by its resulting value, so keeping a field whose current value already matches the reference can pass, as can setting the same title when title preservation is the reference. Per-field `no_unnecessary_write` metrics separately count proposals that set a value already present. `ignore` removes that field from scoring when the reference is ambiguous. Set values for `set` actions. `mustReplace` requires OCR action `set`. OCR key and forbidden facts are case insensitive and compare after whitespace normalization against the resulting OCR: the original content for `keep`, or page text in order for `set`. Reports include matched/total OCR key fact counts and the all-facts case pass rate without copying the private fact strings into reports. `addTagIds` is expected to match exactly; tag coverage and unexpected additions are reported separately. Protected IDs must already occur in `document.tags` and must not be added.

The `expected` object, including its critical flag, is never sent to the model. `notes` is for local curator context and is also never exported.

## Export tasks

```powershell
dotnet run --project src/PaperlessLlm.Eval -- export C:\private\eval\cases.jsonl C:\private\eval\train-export --split train
```

Split selection is mandatory. This keeps holdout cases out of train exports unless `--split holdout` is explicitly supplied. Each `tasks.jsonl` record contains `case_id`, `split`, `page_images`, `system_prompt`, `schema`, and `input`. `--instructions-file PATH` replaces only the system instruction text for an experiment; the production payload builder and schema remain the same.

## Import and score candidate outputs

Import one result per line as `{"case_id":"sample-01","intent":{...}}` (or use `intent_json` with a JSON string). Alternatively, put raw intent JSON in `OUTPUT_DIR/sample-01.json`. Directory import uses only filenames matching case IDs in the selected corpus; it skips `provenance.json` and unrelated files. The importer does not call a runner and supports any inference tool.

```powershell
dotnet run --project src/PaperlessLlm.Eval -- score C:\private\eval\cases.jsonl C:\private\eval\baseline.jsonl --split train --version baseline --model gpt-6-luna-medium --harness codex-exec --out C:\private\eval\baseline-train.json
dotnet run --project src/PaperlessLlm.Eval -- compare --baseline C:\private\eval\baseline-train.json --candidate C:\private\eval\candidate-train.json --out C:\private\eval\comparison.json
```

The scorer first applies the production intent shape checks and `IntentValidator` to each candidate, then checks resulting metadata values, protected tags, OCR replacement requirements, key facts, and forbidden facts. Reports include case level checks, totals, missing and malformed output counts, schema and validator failure counts/codes, partial OCR fact recall, and critical failures. Train and holdout scores are separate invocations and report versions retain model and harness provenance.

For a prompt hillclimb, freeze the corpus, references, and split first. Export and score only `train` while editing an instruction file. Record each hypothesis, prompt hash, model/runtime version, and score; reject regressions and repeat the best candidate to check variability. Select and freeze the winner **before** exporting or inspecting holdout. Compare the frozen winner with baseline on holdout once. Never tune against holdout results; a later iteration needs a fresh reserved set.

The checked-in [synthetic cases](../eval/synthetic-cases.jsonl) exercise metadata correction, missing tags, title preservation, and embedded instructions without private documents. They are text-only smoke cases, not an OCR benchmark. For example:

```powershell
dotnet run --project src/PaperlessLlm.Eval -- export eval/synthetic-cases.jsonl C:\private\eval\smoke --split train --instructions-file eval/prompts/preserve-context-v1.txt
```

Use a separately authenticated inference runner to submit each exported `system_prompt`, `schema`, `input`, and every `page_images` attachment. Return raw intent JSON under the case ID. Do not send `expected`, curator notes, other cases, or Paperless credentials to the model. Keep inference logs and images private. Match the deployment runner when making deployment acceptance claims; identical model names do not establish identical execution conditions.

## Interpretation and limitations

`criticalFailures` counts designated cases failing **any** scored requirement; it does not distinguish an unnecessary title rewrite from a factual or safety error. A production-validator rejection contributes zero matched facts because the proposal cannot be applied. Consequently, this is a valid-proposal fact score, not raw model transcription accuracy. Inspect rejection counts alongside accuracy counts.

Fact matching is a lightweight substring check. It does not establish full transcription, character/word error rate, correct table alignment, amount signs, or the absence of unlisted hallucinations. A short number may match inside another number; punctuation and formatting can also produce false negatives. Use scan comparison and stronger reviewed references for acceptance. Cases marked `mustReplace` must be curated against the actual legibility and scope of the OCR policy; an appropriate abstention can still leave a useful repair unfinished.

The [first tuning report](evaluations/2026-10-01-pilot.md) records a 16/4 real-document pilot and its limitations. Its [selected experimental prompt](../eval/prompts/preserve-context-v1.txt) is available for further offline evaluation. It is **not** the worker's default prompt and has not passed production OCR acceptance.
