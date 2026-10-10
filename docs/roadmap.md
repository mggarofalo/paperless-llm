# Roadmap

[Index](index.md) · [Architecture](architecture.md)

Plane project **PPLLM** owns detailed status and acceptance. This page distinguishes
shipped behavior from planned work; a roadmap entry is not a supported configuration.

## Shipped foundation

Scheduled discovery, durable filesystem jobs, Sign In With ChatGPT through device
codes, one bounded inference request per document, exact-name taxonomy resolution,
validated automatic sync, private evidence/journals and retrospective `needs review`.
The production policy uses existing Paperless OCR and keeps titles/OCR unchanged.

## Local models — PPLLM-27

Add an explicit provider adapter for a **local OpenAI-compatible endpoint**. First
define the supported HTTP dialect and response contract; servers calling themselves
compatible differ in schema support, reasoning fields and error behavior.

Acceptance:

- Configure provider, base URL, model and optional secret-file key without sending
  ChatGPT credentials or Paperless tokens to the local endpoint.
- Keep the same prompt snapshot, intended-state schema, taxonomy resolution and
  validator/synchronizer; transport selection must not weaken write boundaries.
- Bound context, output, deadlines and retries; surface unsupported capabilities.
  No silent local-to-cloud fallback.
- Test network failures, malformed/streamed/truncated output and authentication
  with synthetic fixtures. Verify a fully local inference path with outbound
  provider traffic disabled.
- Run private held-out document evaluations before recommending a local model;
  record accuracy, latency, hardware needs and resource use separately.

## Run-review UI and SQLite settings — PPLLM-28

Deliver in stages:

1. **Read-only run review:** show jobs, attempts, outcomes, errors, prompt/model
   versions, timings and before/after diffs. Show abstentions and no-change outcomes,
   not only successful writes. Link to the Paperless document without duplicating
   its entire document-management UI.
2. **Persisted configuration:** a versioned SQLite database becomes the authority
   for editable settings. Define one-time `.env` import, explicit override precedence,
   validation, migrations and rollback. Keep credentials in a separate secret store.
3. **Tuning controls:** edit schedules, batch size, provider/model and prompt versions;
   expose dry runs and explicit reprocessing. Snapshot effective settings per attempt
   so concurrent changes cannot alter a saved job or sync operation.

Before enabling editing, define UI authentication, CSRF protection, network binding,
authorization, backups and restore tests. Handle worker/UI concurrency and database
transactions explicitly. Do not silently migrate durable job journals to SQLite as
part of a settings change; that is a separate design decision.

Keep automatic validated application with retrospective review as the default.
A UI must not accidentally introduce a mandatory approval queue or reinterpret the
`needs review` tag as job state.

## First-class manual reprocessing — PPLLM-32

Reprocessing should be a normal product operation: submit a selection once,
receive a run ID, disconnect, and check progress later. The planned bulk command
will support previews and useful selections, while a durable request queue lets
users submit work without stopping the worker or launching a container per document.
Run-level progress will distinguish queueing from actual processing and support
safe resumption. Reprocessing stays manual; recurring policies are out of scope.

The existing notes, protected metadata and recovery guarantees remain in force.
Historical enrollment will be an explicit scope; review tags and state-file deletion
will never act as requeue controls. These capabilities are planned, not available
in v0.3.1. Detailed scope, acceptance and delivery order live in Plane PPLLM-32,
its children PPLLM-36/37, and existing progress/UI items PPLLM-30/28.

## Remaining operational work

- **PPLLM-8 / PPLLM-20:** broader live accuracy review, actual write/readback proof
  for the selected policy, sustained operation and token-renewal acceptance.
- **PPLLM-22:** apply OCR recommendations only after effective configuration capture
  and a representative private comparison; OCR research itself does not reprocess
  the library.
- **PPLLM-9:** future receipt-tracker integration must avoid duplicate imports and
  preserve the distinct receipt-logging workflow.
- **PPLLM-29:** retention/compaction of private evidence without deleting recovery records.
- **PPLLM-30:** useful run summaries, abstentions, no-change explanations and provider usage.
- **PPLLM-31:** outgoing-correspondence and ambiguous document-type conventions.
- **PPLLM-32:** manual bulk reprocessing, delivered through PPLLM-36/37
  and existing progress work in PPLLM-30 above.
