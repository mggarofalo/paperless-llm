# Paperless LLM

Reviewable OCR and metadata assistance for Paperless-ngx, using ChatGPT plan
access where supported. Work is tracked in Plane project **PPLLM**.

## Current milestone

This repository starts with a **read-only foundation**:

- Bounded discovery through the authenticated `paperless` CLI.
- Local document/taxonomy snapshots and optional original downloads with SHA-256 hashes.
- Strict local validation of proposed OCR, titles, dates, correspondents, types and tags.
- Synthetic subscription/model probes, separate from real-document processing.

It does **not** run unattended, send your real documents to a model, apply changes,
delete documents, or import receipts. Production model authentication and a tool-free
inference path must be implemented before real-document inference is enabled.

## Install

Requires Python 3.11+ and an authenticated `paperless` CLI on `PATH`.

```powershell
uv sync --extra test
uv run ppllm --help
```

## Read the review queue

```powershell
uv run ppllm discover --limit 5
uv run ppllm --profile default discover --tag "needs review" --limit 5
```

Discovery prints IDs, titles and dates, never full OCR. It does not process the
entire backlog. Limits are 1–100 per invocation; default 10.

## Save a review input

```powershell
uv run ppllm snapshot --document-id 42 --limit 1 --output .local/run-001
uv run ppllm snapshot --document-id 42 --limit 1 --download-originals --output .local/run-002
```

Each output directory must be new. `snapshot.json` includes document contents and
metadata, the current taxonomy, and optional original-file hashes. Its presence
marks a completed snapshot. A detected concurrent document change aborts collection.

Snapshots contain private data. Keep them in a directory restricted to your OS
account. `.local/` and `runs/` are ignored by Git; arbitrary output paths are your
responsibility. Windows permissions inherit from the parent directory. This version
does not enforce encryption, retention, or deletion of completed snapshots.

Original files keep their bytes and are saved with a neutral `.original` extension;
page rendering and embedded PDF OCR replacement are not implemented.

## Validate a proposal locally

```powershell
uv run ppllm validate --snapshot .local/run-001/snapshot.json --document-id 42 --proposal proposal.json
```

The schema and prompt builder live in `paperless_llm.proposals`. Validation means
the proposal fits the configured constraints; it does not establish factual accuracy.
Null fields mean abstain, never clear. Tags are additive proposals; there is no
replacement of the whole tag set and no apply command.

The CLI currently validates metadata-only proposals (`ocr_text: null`). The library
requires explicit visual-source provenance for OCR proposals; having an original
file in a snapshot does not establish that an inference run actually inspected it.

The policy protects review/logging state, HSA reimbursement state, expense and
property tags. New taxonomy entries need separate review. Scanned text is untrusted
data, not an instruction to run commands.

## Check subscription access with synthetic data

Requires a compatible Codex CLI logged in with ChatGPT. Status does not perform
inference; each probe uses one small subscription request.

```powershell
uv run ppllm auth-status
uv run ppllm probe --model gpt-6-luna
uv run ppllm probe --model gpt-6-sol
```

The probe generates its own fixed image and accepts no document/prompt input.
It rejects API-key login and does not fall back to separately billed API access.
The installed Codex CLI has not established an all-tools-off boundary, so a
successful synthetic probe does **not** enable real-document inference.

## Development

```powershell
uv run pytest
uv build
```

Use synthetic fixtures and never commit personal scans, OCR, tokens or run output.
See [AGENTS.md](AGENTS.md), [architecture](docs/architecture.md), and
[connector details](docs/connector.md).

Next milestones: production subscription authentication, source-page rendering and
evidence, reviewed application/rollback, a durable worker, a measured pilot, and
duplicate-safe handoff to the custom receipts app.
