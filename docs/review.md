# Review and recovery

[Home](../README.md) · [Operation](operations.md) · [Architecture](architecture.md)

The worker applies validated metadata changes and adds `needs review` in the same PATCH. A proposed note is appended through a separate POST after that PATCH. Review those documents in Paperless, then remove the marker when satisfied. A no-op never adds it, and clearing it does not trigger inference or restore it. Receipt-tracker `inbox` and `receipt to log` remain independent workflow state.

## What can change

Each proposed title, date, correspondent, document type and descriptive tag needs document-specific evidence. Exact taxonomy names resolve to unique existing IDs. The worker keeps Paperless OCR unchanged; its default prompt also keeps titles unchanged. Ambiguous fields can stay unchanged while other supported fields are updated.

The model cannot remove tags, clear correspondent/type assignments, create taxonomy, change ownership or permissions, replace originals, delete documents or import receipts. The [prompt file](prompts.md) explains these boundaries, and deterministic validation enforces the allowed shape and references. Schema and evidence checks do not prove semantic correctness.

## Document notes

Starting in v0.3.0: the default prompt can propose one short
summary of useful facts from existing OCR. Typical content includes repairs and
warranty terms, significant purchased items, distinct billed/paid/patient amounts,
contract periods, or a letter's decision and deadline. It describes what the
document states using absolute dates, without inferring current debt, reimbursement,
HSA eligibility, tax deductibility or completed workflows.

The worker adds the heading `AI-generated document summary (Paperless LLM)`.
The body is one plain-text paragraph, usually 40–100 words and at most 1,200
characters. Notes can be skipped when they would add little value or OCR is unclear.
Truncated OCR or an unavailable notes collection cannot support a new note.
Each note requires exact quotations present in OCR; checking these quotations
does not prove that every claim or interpretation is correct. Inspect the scan
before relying on important amounts or terms. Human note text is retained in
private state but is not sent to the model; the model receives availability and
existing-summary flags only.

Notes are append-only. The worker never edits or deletes a note. Any existing note
beginning with the generated heading suppresses another summary, including on
explicit reprocessing. Preserve the heading if you manually correct a summary.
Removing the heading or deleting the note allows a future explicit reprocessing
to propose another. A skipped note with unchanged metadata never adds `needs review`.

The Notes API has no idempotency key. The private operation journal records the
verified metadata state and persists `NoteAttempted` **before** sending POST.
After a lost response, a retry recognizes an exact matching note without posting
again. If an attempted note remains absent, the job stops with `sync_conflict`
instead of risking a duplicate from a still-running request. The private journal
may show `metadata_verified`: metadata and the review marker can already be applied
even though the note is unresolved. A crash between recording the attempt and
sending it also requires inspection. This intentionally favors no duplicate POST
over automatic recovery in that small window.

For an unresolved note, stop the worker, inspect the Paperless Notes and private
journal, and establish that the original request has finished. If the exact note
exists, `retry ID` can reconcile it. Reprocessing now accepts only completed jobs;
it cannot bypass a failed or uncertain journal, even when a note appears absent.
An absent attempted note needs explicit journal recovery, which has no automatic
fresh-proposal command yet. Preserve the evidence for inspection. Dry runs never
send a note or mark an absent attempt as complete.

The source check and POST are separate requests. One worker/state volume per
instance is required for duplicate prevention; independent workers and concurrent
human edits are not atomically coordinated by Paperless. Notes require the
additional [service-account permissions](authentication.md#paperless-account).

## History and journals

Use Paperless's document history to inspect original and updated values, with audit history enabled and retained on your instance. Restore fields through Paperless as needed, taking later human edits into account. This release does not provide automatic undo.

Each job retains private source and proposal evidence. `/data/audit/operations` holds a journal with the before state, intended patch and verified result. The worker writes pending intent before PATCH and reads back afterward. If the connection fails after Paperless accepts the write, a retry first checks whether the intended state is already present. It confirms that result without sending a duplicate write. It refuses to replay against a changed source and revalidates taxonomy before a pending replay.

Sync makes a final source revision check immediately before PATCH. These are separate HTTP requests, not an atomic compare-and-swap. A concurrent edit in that gap can race with the update. Unexpected readback becomes a visible failure, but cannot retroactively prevent that race. Avoid manually editing a document while its job is running.

The synthetic test suite exercises these recovery paths. A representative document accuracy evaluation and live authorization-renewal check remain separate deployment acceptance work.
