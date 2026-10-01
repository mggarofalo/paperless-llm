# Offline intent evaluation

`PaperlessLlm.Eval` exports prompt cases and scores externally generated intent JSON. It has no network, Paperless client, authentication, or model runner. It references the production `IntentPrompt.Build`, `DocumentIntent.Schema`, and `IntentValidator` directly. It does not alter the production prompt.

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

For a prompt hillclimb, export and score the `train` split while editing a private instruction file. Evaluate finalists once on holdout, then freeze the winning prompt and report. Never tune against the holdout scores.
