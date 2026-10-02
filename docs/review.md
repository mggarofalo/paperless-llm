# Review and recovery

[Home](../README.md) · [Operation](operations.md) · [Architecture](architecture.md)

The worker applies validated changes and adds `needs review` in the same PATCH. Review those documents in Paperless, then remove the marker when satisfied. A no-op never adds it, and clearing it does not trigger inference or restore it. Receipt-tracker `inbox` and `receipt to log` remain independent workflow state.

## What can change

Each proposed title, date, correspondent, document type and descriptive tag needs document-specific evidence. Exact taxonomy names resolve to unique existing IDs. The worker keeps Paperless OCR unchanged; its default prompt also keeps titles unchanged. Ambiguous fields can stay unchanged while other supported fields are updated.

The model cannot remove tags, clear correspondent/type assignments, create taxonomy, change ownership or permissions, replace originals, delete documents or import receipts. The [prompt file](prompts.md) explains these boundaries, and deterministic validation enforces the allowed shape and references. Schema and evidence checks do not prove semantic correctness.

## History and journals

Use Paperless's document history to inspect original and updated values, with audit history enabled and retained on your instance. Restore fields through Paperless as needed, taking later human edits into account. This release does not provide automatic undo.

Each job retains private source and proposal evidence. `/data/audit/operations` holds a journal with the before state, intended patch and verified result. The worker writes pending intent before PATCH and reads back afterward. If the connection fails after Paperless accepts the write, a retry first checks whether the intended state is already present. It confirms that result without sending a duplicate write. It refuses to replay against a changed source and revalidates taxonomy before a pending replay.

Sync makes a final source revision check immediately before PATCH. These are separate HTTP requests, not an atomic compare-and-swap. A concurrent edit in that gap can race with the update. Unexpected readback becomes a visible failure, but cannot retroactively prevent that race. Avoid manually editing a document while its job is running.

The synthetic test suite exercises these recovery paths. A representative document accuracy evaluation and live authorization-renewal check remain separate deployment acceptance work.
