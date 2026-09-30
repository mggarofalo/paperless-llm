# Reviewing accuracy

[Home](../README.md) · [Operations](operations.md) · [Architecture](architecture.md)

The worker saves private evidence for each review attempt. It never writes the
proposal back to Paperless. A valid schema is a structural check, not proof that
an OCR transcription or classification is correct.

## Open the reports

Copy the audit directory from the running worker to a private local folder:

```sh
mkdir -p review
docker compose cp worker:/data/audit/. ./review/
```

Open an attempt's `review.html` in your browser. Keep the entire attempt directory
so its page images and original source remain available. If Docker runs remotely,
copy the folder to your laptop over SSH rather than exposing an unauthenticated
web server. These files contain your documents, existing OCR, proposed OCR and
metadata; do not put them in a public directory or repository.

Each attempt includes a machine-readable JSON record and a readable HTML report,
with the source revision, model/prompt information, current metadata and OCR,
proposed fields, evidence and uncertainty. Originals and rendered pages are
retained with hashes. Rejected output is retained as evidence but is not marked
as an accepted proposal. HTML text is escaped and external content is blocked.

## What to check

Compare the proposed OCR directly with every source page. Pay particular attention
to names, dates, totals, signs, account numbers and handwriting. Check the
correspondent and document type against the document itself, not an incidental
logo or address. `null` means the model abstained; it does not mean delete the
existing field.

The validator rejects unknown taxonomy IDs, malformed dates, unsupported fields,
protected workflow-tag additions and OCR without visual evidence. It cannot prove
that cited evidence is true. Review tags, receipt logging, HSA status, expense and
property assignments stay under your control.

For an initial pilot, review a small explicit backfill and record accept/reject
judgments separately. Measure accepted suggestions divided by reviewed suggestions,
with a separate count of abstentions and failures. Do not count unreviewed jobs as
accurate or treat a model's expressed confidence as an accuracy metric.

This release has no annotation dashboard or automatic accuracy score. The saved
JSON is the basis for later evaluation tooling; the HTML is for manual review.
